using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Stream.Video.v1.Sfu.Events;
using Stream.Video.v1.Sfu.Models;
using Stream.Video.v1.Sfu.Signal;
using StreamVideo.Core.LowLevelClient.WebSockets;
using StreamVideo.Core.Models;
using StreamVideo.Core.Models.Sfu;
using StreamVideo.Core.StatefulModels;
using StreamVideo.Core.StatefulModels.Tracks;
using StreamVideo.Core.Utils;
using StreamVideo.Libs.Http;
using StreamVideo.Libs.Logs;
using StreamVideo.Libs.Serialization;
using StreamVideo.Libs.Utils;
using Unity.WebRTC;
using UnityEngine;
using ICETrickle = Stream.Video.v1.Sfu.Models.ICETrickle;
using Random = System.Random;
using TrackType = StreamVideo.Core.Models.Sfu.TrackType;
using TrackTypeInternal = Stream.Video.v1.Sfu.Models.TrackType;

namespace StreamVideo.Core.LowLevelClient
{
    public delegate void ParticipantTrackChangedHandler(IStreamVideoCallParticipant participant, IStreamTrack track);

    //StreamTodo: reconnect flow needs to send `UpdateSubscription` https://getstream.slack.com/archives/C022N8JNQGZ/p1691139853890859?thread_ts=1691139571.281779&cid=C022N8JNQGZ

    //StreamTodo: decide lifetime, if the obj persists across session maybe it should be named differently and only return struct handle to a session
    internal sealed class RtcSession : IMediaInputProvider, IDisposable
    {
        public CallingState CallState
        {
            get => _callState;
            private set
            {
                if (_callState == value)
                {
                    return;
                }

                var prevState = _callState;
                _callState = value;
                _logs.Warning($"Call state changed from {prevState} to {value}");
            }
        }
        
        #region IInputProvider
        
        //StreamTodo: move IInputProvider elsewhere. it's for easy testing only
        public AudioSource AudioInput { get; set; }
        public WebCamTexture VideoInput { get; set; }
        
        #endregion

        public string SessionId { get; private set; }

        public RtcSession(SfuWebSocket sfuWebSocket, ILogs logs, ISerializer serializer, IHttpClient httpClient)
        {
            _serializer = serializer;
            _httpClient = httpClient;
            _logs = logs;

            //StreamTodo: SFU WS should be created here so that RTC session owns it
            _sfuWebSocket = sfuWebSocket ?? throw new ArgumentNullException(nameof(sfuWebSocket));
            _sfuWebSocket.JoinResponse += OnSfuJoinResponse;
            _sfuWebSocket.IceTrickle += OnSfuIceTrickle;
            _sfuWebSocket.SubscriberOffer += OnSfuSubscriberOffer;
            _sfuWebSocket.TrackPublished += OnSfuTrackPublished;
            _sfuWebSocket.TrackUnpublished += OnSfuTrackUnpublished;
        }

        public void Dispose()
        {
            StopAsync().LogIfFailed();

            _sfuWebSocket.JoinResponse -= OnSfuJoinResponse;
            _sfuWebSocket.IceTrickle -= OnSfuIceTrickle;
            _sfuWebSocket.SubscriberOffer -= OnSfuSubscriberOffer;
            _sfuWebSocket.TrackPublished -= OnSfuTrackPublished;
            _sfuWebSocket.TrackUnpublished -= OnSfuTrackUnpublished;
            _sfuWebSocket.Dispose();

            DisposeSubscriber();
            DisposePublisher();
        }

        public void Update()
        {
            _sfuWebSocket.Update();
            _publisher?.Update();

            //StreamTodo: we could remove this if we'd maintain a collection of tracks and update them directly
            if (_activeCall != null)
            {
                foreach (StreamVideoCallParticipant p in _activeCall.Participants)
                {
                    p.Update();
                }
            }
        }

        public async Task StartAsync(StreamCall call)
        {
            if (_activeCall != null)
            {
                throw new InvalidOperationException(
                    $"Cannot start new session until previous call is active. Active call: {_activeCall}");
            }

            _activeCall = call ?? throw new ArgumentNullException(nameof(call));

            CleanUpSession();

            CallState = CallingState.Joining;

            var sfuUrl = call.Credentials.Server.Url;
            var sfuToken = call.Credentials.Token;
            var iceServers = call.Credentials.IceServers;

            CreateSubscriber(iceServers);

            if (CanPublish())
            {
                CreatePublisher(iceServers);
            }

            SessionId = Guid.NewGuid().ToString();
            _logs.Warning($"START Session: " + SessionId);

            // We don't set initial offer as local. Later on we set generated answer as a local
            var offer = await _subscriber.CreateOfferAsync();

            _sfuWebSocket.SetSessionData(SessionId, offer.sdp, sfuUrl, sfuToken);
            await _sfuWebSocket.ConnectAsync();

            while (CallState != CallingState.Joined)
            {
                //StreamTodo: implement a timeout if something goes wrong
                //StreamTodo: implement cancellation token
                await Task.Delay(1);
            }

            await SubscribeToTracksAsync();

            //StreamTodo: validate when this state should set
            CallState = CallingState.Joined;
        }

        public async Task StopAsync()
        {
            CleanUpSession();
            //StreamTodo: check with js definition of "offline" 
            CallState = CallingState.Offline;
            await _sfuWebSocket.DisconnectAsync(WebSocketCloseStatus.NormalClosure, "Video session stopped");
        }

        //StreamTodo: call by call.reconnectOrSwitchSfu()
        public void Reconnect()
        {
            _subscriber?.RestartIce();
            _publisher?.RestartIce();
        }

        private readonly SfuWebSocket _sfuWebSocket;
        private readonly ISerializer _serializer;
        private readonly ILogs _logs;

        private readonly List<ICETrickle> _pendingIceTrickleRequests = new List<ICETrickle>();

        private StreamCall _activeCall;
        private IHttpClient _httpClient;
        private CallingState _callState;

        private StreamPeerConnection _subscriber;
        private StreamPeerConnection _publisher;

        private void CleanUpSession()
        {
            _pendingIceTrickleRequests.Clear();
        }

        private async Task SubscribeToTracksAsync()
        {
            _logs.Info("Request SFU - UpdateSubscriptionsRequest");
            var tracks = GetDesiredTracksDetails();

            var request = new UpdateSubscriptionsRequest
            {
                SessionId = SessionId,
            };

            request.Tracks.AddRange(tracks);

            var response = await RpcCallAsync(request, GeneratedAPI.UpdateSubscriptions,
                nameof(GeneratedAPI.UpdateSubscriptions));

            if (response.Error != null)
            {
                _logs.Error(response.Error.Message);
            }
        }

        private IEnumerable<TrackSubscriptionDetails> GetDesiredTracksDetails()
        {
            //StreamTodo: inject info on what tracks we want and what dimensions
            var trackTypes = new[] { TrackTypeInternal.Video, TrackTypeInternal.Audio };

            foreach (var participant in _activeCall.Participants)
            {
                foreach (var trackType in trackTypes)
                {
                    yield return new TrackSubscriptionDetails
                    {
                        UserId = participant.UserId,
                        SessionId = participant.SessionId,

                        TrackType = trackType,
                        Dimension = new VideoDimension
                        {
                            Width = 1200,
                            Height = 1200
                        }
                    };
                }
            }
        }

        private async Task SendIceCandidateAsync(RTCIceCandidate candidate, StreamPeerType streamPeerType)
        {
            try
            {
                var iceTrickle = new ICETrickle
                {
                    PeerType = streamPeerType.ToPeerType(),
                    IceCandidate = _serializer.Serialize(candidate),
                    SessionId = SessionId,
                };

                if (_callState == CallingState.Joined)
                {
                    await RpcCallAsync(iceTrickle, GeneratedAPI.IceTrickle, nameof(GeneratedAPI.IceTrickle));
                }
                else
                {
                    _pendingIceTrickleRequests.Add(iceTrickle);
                }
            }
            catch (Exception e)
            {
                _logs.Exception(e);
            }
        }

        private void OnSfuJoinResponse(JoinResponse joinResponse)
        {
            _logs.InfoIfDebug($"Handle Sfu {nameof(JoinResponse)}");
            ((StreamCall)_activeCall).UpdateFromSfu(joinResponse);
            OnSfuJoinedCall();
        }

        private void OnSfuJoinedCall()
        {
            CallState = CallingState.Joined;

            foreach (var iceTrickle in _pendingIceTrickleRequests)
            {
                RpcCallAsync(iceTrickle, GeneratedAPI.IceTrickle, nameof(GeneratedAPI.IceTrickle)).LogIfFailed();
            }
        }

        private void OnSfuIceTrickle(ICETrickle iceTrickle)
        {
            //StreamTodo: better to wrap in separate structure and not depend on a specific WebRTC implementation
            var iceCandidateInit = _serializer.Deserialize<RTCIceCandidateInit>(iceTrickle.IceCandidate);

            switch (iceTrickle.PeerType.ToStreamPeerType())
            {
                case StreamPeerType.Publisher:
                    _publisher.AddIceCandidate(iceCandidateInit);
                    break;
                case StreamPeerType.Subscriber:
                    _subscriber.AddIceCandidate(iceCandidateInit);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /**
     * This is called when the SFU sends us an offer
     * - Sets the remote description
     * - Creates an answer
     * - Sets the local description
     * - Sends the answer back to the SFU
     */
        private async void OnSfuSubscriberOffer(SubscriberOffer subscriberOffer)
        {
            //StreamTodo: check RtcSession.kt handleSubscriberOffer for the retry logic

            try
            {
                //StreamTodo: handle subscriberOffer.iceRestart
                var rtcSessionDescription = new RTCSessionDescription
                {
                    type = RTCSdpType.Offer,
                    sdp = subscriberOffer.Sdp
                };

                await _subscriber.SetRemoteDescriptionAsync(rtcSessionDescription);

                var answer = await _subscriber.CreateAnswerAsync();

                //StreamTodo: mangle SDP

                await _subscriber.SetLocalDescriptionAsync(ref answer);

                var sendAnswerRequest = new SendAnswerRequest
                {
                    PeerType = PeerType.Subscriber,
                    Sdp = answer.sdp,
                    SessionId = SessionId
                };

                await RpcCallAsync(sendAnswerRequest, GeneratedAPI.SendAnswer, nameof(GeneratedAPI.SendAnswer),
                    preLog: true);
            }
            catch (Exception e)
            {
                _logs.Exception(e);
            }
        }

        private void OnSfuTrackUnpublished(TrackUnpublished trackUnpublished)
        {
            var userId = trackUnpublished.UserId;
            var sessionId = trackUnpublished.SessionId;
            var type = trackUnpublished.Type.ToPublicEnum();
            var cause = trackUnpublished.Cause;

            // Optionally available. Read TrackUnpublished.participant comment in events.proto
            var participant = trackUnpublished.Participant;

            UpdateParticipantTracksState(userId, sessionId, type, isEnabled: false, out var streamParticipant);

            if (participant != null && streamParticipant != null)
            {
                streamParticipant.UpdateFromSfu(participant);
            }
        }

        private void OnSfuTrackPublished(TrackPublished trackPublished)
        {
            var userId = trackPublished.UserId;
            var sessionId = trackPublished.SessionId;
            var type = trackPublished.Type.ToPublicEnum();

            // Optionally available. Read TrackUnpublished.participant comment in events.proto
            var participant = trackPublished.Participant;

            UpdateParticipantTracksState(userId, sessionId, type, isEnabled: true, out var streamParticipant);

            if (participant != null && streamParticipant != null)
            {
                streamParticipant.UpdateFromSfu(participant);
            }
        }

        private void UpdateParticipantTracksState(string userId, string sessionId, TrackType trackType, bool isEnabled,
            out StreamVideoCallParticipant participant)
        {
            participant = (StreamVideoCallParticipant)_activeCall.Participants.FirstOrDefault(p
                => p.SessionId == sessionId);
            if (participant == null)
            {
                _logs.Error($"Failed to find participant with session ID: `{sessionId}` and user ID: `{userId}`");
                return;
            }

            participant.SetTrackEnabled(trackType, isEnabled);
        }

        
        //StreamTodo: implement retry strategy like in Android SDK
        private async Task<TResponse> RpcCallAsync<TRequest, TResponse>(TRequest request,
            Func<HttpClient, TRequest, Task<TResponse>> rpcCallAsync, string debugRequestName, bool preLog = false)
        {
            var serializedRequest = _serializer.Serialize(request);

            if (preLog)
            {
                _logs.Warning($"[RPC REQUEST START] {debugRequestName} {serializedRequest}");
            }

            //StreamTodo: use injected client or cache this one
            var connectUrl = _activeCall.Credentials.Server.Url.Replace("/twirp", "");

            //StreamTodo: move headers population logic elsewhere + remove duplication with main client
            var httpClient = new HttpClient()
            {
                DefaultRequestHeaders =
                {
                    { "stream-auth-type", "jwt" },
                    { "X-Stream-Client", "stream-video-unity-client-0.1.0" }
                }
            };

            httpClient.DefaultRequestHeaders.Authorization
                = new AuthenticationHeaderValue(_activeCall.Credentials.Token);
            httpClient.BaseAddress = new Uri(connectUrl);

            var response = await rpcCallAsync(httpClient, request);
            var serializedResponse = _serializer.Serialize(response);

#if STREAM_DEBUG_ENABLED
            //StreamTodo: move to debug helper class
            var sb = new StringBuilder();

            var errorProperty = typeof(TResponse).GetProperty("Error");
            var error = (Stream.Video.v1.Sfu.Models.Error)errorProperty.GetValue(response);
            var errorLog = error != null ? $"<color=red>{error.Message}</color>" : "";
            var errorStatus = error != null ? "<color=red>FAILED</color>" : "<color=green>SUCCESS</color>";
            sb.AppendLine($"[RPC Request] {errorStatus} {debugRequestName} | {errorLog}");
            sb.AppendLine(serializedRequest);
            sb.AppendLine();
            sb.AppendLine("Response:");
            sb.AppendLine(serializedResponse);

            _logs.Warning(sb.ToString());
#endif

            return response;
        }

        //StreamTodo: subscribe to changes in capabilities. This can potentially change during the call
        private bool CanPublish()
            => _activeCall != null &&
               _activeCall.OwnCapabilities.Any(c => c == OwnCapability.SendVideo || c == OwnCapability.SendAudio);

        /**
     * https://developer.mozilla.org/en-US/docs/Web/API/RTCPeerConnection/negotiationneeded_event
     *
     * Is called whenever a negotiation is needed. Common examples include
     * - Adding a new media stream
     * - Adding an audio Stream
     * - A screenshare track is started
     *
     * Creates a new SDP
     * - And sets it on the localDescription
     * - Enables video simulcast
     * - calls setPublisher
     * - sets setRemoteDescription
     *
     * Retry behaviour is to retry 3 times quickly as long as
     * - the sfu didn't change
     * - the sdp didn't change
     * If that fails ask the call monitor to do an ice restart
     */
        private async void OnPublisherNegotiationNeeded()
        {
            Debug.LogWarning("OnPublisherNegotiationNeeded");
            try
            {
                var offer = await _publisher.CreateOfferAsync();
                await _publisher.SetLocalDescriptionAsync(ref offer);

                //StreamTodo: timeout + break if we're disconnecting/reconnecting
                while (_sfuWebSocket.ConnectionState != ConnectionState.Connected)
                {
                    await Task.Delay(1);
                }

                var tracks = GetPublisherTracks();

                //StreamTodo: mangle SDP
                var request = new SetPublisherRequest
                {
                    Sdp = offer.sdp,
                    SessionId = SessionId,
                };
                request.Tracks.AddRange(tracks);

                var result = await RpcCallAsync(request, GeneratedAPI.SetPublisher, nameof(GeneratedAPI.SetPublisher));

                await _publisher.SetRemoteDescriptionAsync(new RTCSessionDescription()
                {
                    type = RTCSdpType.Answer,
                    sdp = result.Sdp
                });
            }
            catch (Exception e)
            {
                _logs.Exception(e);
            }
        }

        private IEnumerable<TrackInfo> GetPublisherTracks()
        {
            //StreamTodo: get resolution from some IMediaDeviceProvider / IMediaSourceProvider
            var captureResolution = (Width: 1920, Height: 1080);

            var senderTracks = _publisher.GetTransceivers().ToArray();
            
            // var senderTracks = _publisher.GetTransceivers().Where(t
            //     => t.Direction == RTCRtpTransceiverDirection.SendOnly && t.Sender?.Track != null).ToArray();

            _logs.Warning($"GetPublisherTracks - transceivers: {senderTracks?.Count()} ");

            //StreamTodo: figure out TrackType, because we rely on transceiver track type mapping we don't support atm screen video/audio share tracks
            //This implementation is based on the Android SDK, perhaps we shouldn't rely on GetTransceivers() but maintain our own TrackType => Transceiver mapping

            foreach (var transceiver in senderTracks)
            {
                //StreamTodo: remove this. Skip for now due to `invalid SetPublisher request: track c59b906b-96a5-4d3f-8bed-166f16c284ef: audio cannot have simulcast layers` RPC error
                if (transceiver.Sender.Track.Kind == TrackKind.Audio)
                {
                    continue;
                }
                
                var videoLayers = GetVideoLayers(transceiver, captureResolution);
                _logs.Warning($"Video layers: {videoLayers.Count()} for transceiver: {transceiver.Sender.Track.Kind}");
                var trackInfo = new TrackInfo
                {
                    TrackId = transceiver.Sender.Track.Id,
                    TrackType = transceiver.Sender.Track.Kind.ToInternalEnum(),
                };
                trackInfo.Layers.AddRange(videoLayers);
                yield return trackInfo;
            }
        }

        private IEnumerable<VideoLayer> GetVideoLayers(RTCRtpTransceiver transceiver,
            (int Width, int Height) captureResolution)
        {
            foreach (var encoding in transceiver.Sender.GetParameters().encodings)
            {
                var scaleBy = encoding.scaleResolutionDownBy ?? 1.0;
                var width = (uint)(captureResolution.Width / scaleBy);
                var height = (uint)(captureResolution.Height / scaleBy);

                var quality = EncodingsToVideoQuality(encoding);

                yield return new VideoLayer
                {
                    Rid = encoding.rid,
                    VideoDimension = new VideoDimension
                    {
                        Width = width,
                        Height = height
                    },
                    Bitrate = (uint)(encoding.maxBitrate ?? 0),
                    Fps = 24, //StreamTodo: hardcoded value, should integrator set this?
                    Quality = quality,
                };
            }
        }

        private static VideoQuality EncodingsToVideoQuality(RTCRtpEncodingParameters encodings)
        {
            switch (encodings.rid)
            {
                case "f": return VideoQuality.High;
                case "h": return VideoQuality.Mid;
                default: return VideoQuality.LowUnspecified;
            }
        }

        private void OnIceTrickled(RTCIceCandidate iceCandidate, StreamPeerType peerType)
        {
            SendIceCandidateAsync(iceCandidate, peerType).LogIfFailed();
        }

        private void OnSubscriberStreamAdded(MediaStream mediaStream)
        {
            var idParts = mediaStream.Id.Split(":");
            var trackPrefix = idParts[0];
            var trackTypeKey = idParts[1];

#if STREAM_DEBUG_ENABLED
            _logs.Warning($"Subscriber stream received, trackPrefix: {trackPrefix}, trackTypeKey: {trackTypeKey}");
#endif

            var participant = _activeCall.Participants.SingleOrDefault(p => p.TrackLookupPrefix == trackPrefix);
            if (participant == null)
            {
                //StreamTodo: figure out severity of this case. Perhaps it's not an error, maybe we haven't received coordinator event yet like ParticipantJoined
                _logs.Warning(
                    $"Failed to find participant with trackPrefix: {trackPrefix} for media stream with ID: {mediaStream.Id}");
                return;
            }

            if (!TrackTypeExt.TryGetTrackType(trackTypeKey, out var trackType))
            {
                _logs.Error(
                    $"Failed to get {typeof(TrackType)} for value: {trackTypeKey} on media stream with ID: {mediaStream.Id}");
                return;
            }

            if (trackType == TrackType.Unspecified)
            {
                _logs.Error(
                    $"Unexpected {nameof(trackType)} of value: {trackType} on media stream with ID: {mediaStream.Id}");
                return;
            }

            //StreamTodo: assert that we expect exactly one track per type.
            //In theory stream can contain multiple tracks but we're extracting track type from stream ID so I assume it always has to be exactly one track

            foreach (var track in mediaStream.GetAudioTracks())
            {
                //StreamTodo: verify why this is needed. Taken from Android SDK
                track.Enabled = true;
            }

            var internalParticipant = ((StreamVideoCallParticipant)participant);

            foreach (var track in mediaStream.GetTracks())
            {
                internalParticipant.SetTrack(trackType, track, out var streamTrack);
                _activeCall.NotifyTrackAdded(internalParticipant, streamTrack);
            }
        }

        private void CreateSubscriber(IEnumerable<ICEServer> iceServers)
        {
            _subscriber = new StreamPeerConnection(_logs, StreamPeerType.Subscriber, iceServers, MediaStreamIdFactory, this);
            _subscriber.IceTrickled += OnIceTrickled;
            _subscriber.StreamAdded += OnSubscriberStreamAdded;
        }

        private void DisposeSubscriber()
        {
            if (_subscriber != null)
            {
                _subscriber.IceTrickled -= OnIceTrickled;
                _subscriber.StreamAdded -= OnSubscriberStreamAdded;
                _subscriber.Dispose();
                _subscriber = null;
            }
        }

        private string MediaStreamIdFactory(TrackKind trackKind)
        {
            var localParticipant = _activeCall.Participants.Single(p => p.SessionId == SessionId);
            var trackPrefix = localParticipant.TrackLookupPrefix;
            var trackType = trackKind.ToInternalEnum();

            //StreamTodo: revise that, not sure what's the point of the random number here if the (trackPrefix, trackType) should be a unique pair
            var randomNumber = new Random().Next();
            var id = $"{trackPrefix}:{trackType}:{randomNumber}";
            return id;
        }

        /// <summary>
        /// Creating publisher requires active <see cref="IStreamCall"/>
        /// </summary>
        private void CreatePublisher(IEnumerable<ICEServer> iceServers)
        {
            //StreamTodo: Handle default settings -> speaker off, mic off, cam off
            var callSettings = _activeCall.Settings;

            _publisher = new StreamPeerConnection(_logs, StreamPeerType.Publisher, iceServers, MediaStreamIdFactory, this);
            _publisher.IceTrickled += OnIceTrickled;
            _publisher.NegotiationNeeded += OnPublisherNegotiationNeeded;
        }

        private void DisposePublisher()
        {
            if (_publisher != null)
            {
                _publisher.IceTrickled -= OnIceTrickled;
                _publisher.NegotiationNeeded -= OnPublisherNegotiationNeeded;
                _publisher.Dispose();
                _publisher = null;
            }
        }
    }
}