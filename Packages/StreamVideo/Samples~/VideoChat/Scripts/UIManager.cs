using System;
using System.Collections.Generic;
using System.Linq;
using StreamVideo.Core.StatefulModels;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StreamVideo.ExampleProject
{
    public class UIManager : MonoBehaviour
    {
        [Header("Video Sending Settings")]
        [SerializeField]
        private int _senderVideoWidth = 1280;

        [SerializeField]
        private int _senderVideoHeight = 720;

        [SerializeField]
        private int _senderVideoFps = 30;

        public void AddParticipant(IStreamVideoCallParticipant participant)
        {
            var view = Instantiate(_participantViewPrefab, _participantsContainer);
            view.Init(participant);
            _participantSessionIdToView.Add(participant.SessionId, view);

            if (participant.IsLocalParticipant)
            {
                // Set input camera as a video source for local participant - we won't receive OnTrack event for local participant
                view.SetLocalCameraSource(_activeCamera);
            }
        }

        public void RemoveParticipant(string sessionId, string userId)
        {
            if (!_participantSessionIdToView.TryGetValue(sessionId, out var view))
            {
                Debug.LogError("Failed to find view for removed participant with sessionId: " + sessionId);
                return;
            }

            _participantSessionIdToView.Remove(sessionId);
            Destroy(view.gameObject);
        }

        public void DominantSpeakerChanged(IStreamVideoCallParticipant currentDominantSpeaker,
            IStreamVideoCallParticipant previousDominantSpeaker)
        {
            Debug.Log(
                $"Dominant speaker changed from {currentDominantSpeaker.Name} to {previousDominantSpeaker?.Name}");

            foreach (var participantView in _participantSessionIdToView.Values)
            {
                var isDominantSpeaker = participantView.Participant == currentDominantSpeaker;
                participantView.UpdateIsDominantSpeaker(isDominantSpeaker);
            }
        }

        public void SetJoinCallId(string joinCallId) => _joinCallIdInput.text = joinCallId;

        public void SetActiveCall(IStreamCall call)
        {
            _activeCall = call;
            
            var isActive = call != null;
            var isCallOwner = isActive && call.IsLocalUserOwner;

            // If call is active show end/leave button, otherwise show join/create buttons
            _beforeCallButtonsContainer.gameObject.SetActive(!isActive);
            _duringCallButtonsContainer.gameObject.SetActive(isActive);

            // If local user is the call owner we can "end" the call, otherwise we can only "leave" the call
            _endBtn.gameObject.SetActive(isCallOwner);

            if (!isActive)
            {
                ClearAllParticipants();
            }
        }

        protected void Awake()
        {
            _videoManager = GetComponent<StreamVideoManager>();
            _videoManager.CallStarted += OnCallStarted;
            _videoManager.CallEnded += OnCallEnded;

            _joinBtn.onClick.AddListener(OnJoinCallButtonClicked);
            _createBtn.onClick.AddListener(OnCreateAndJoinCallButtonClicked);
            _leaveBtn.onClick.AddListener(_videoManager.LeaveActiveCall);
            _endBtn.onClick.AddListener(_videoManager.EndActiveCall);

            _audioRedToggle.onValueChanged.AddListener(_videoManager.SetAudioREDundancyEncoding);
            _audioDtxToggle.onValueChanged.AddListener(_videoManager.SetAudioDtx);

            _microphoneDeviceDropdown.ClearOptions();
            _microphoneDeviceDropdown.onValueChanged.AddListener(OnMicrophoneDeviceChanged);
            _microphoneDeviceDropdown.AddOptions(Microphone.devices.ToList());

            _cameraDeviceDropdown.ClearOptions();
            _cameraDeviceDropdown.onValueChanged.AddListener(OnCameraChanged);
            var cameraDevices = WebCamTexture.devices;

            foreach (var device in cameraDevices)
            {
                _cameraDeviceDropdown.options.Add(new TMP_Dropdown.OptionData(device.name));
            }

            SmartPickDefaultCamera();
            SmartPickDefaultMicrophone();
        }

        protected void Start()
        {
            _microphoneDeviceToggle.onValueChanged.AddListener(OnMicrophoneToggled);
            OnMicrophoneToggled(_microphoneDeviceToggle.enabled);

            //StreamTodo: handle camera toggle
        }

        protected void OnDestroy()
        {
            _videoManager.CallStarted -= OnCallStarted;
            _videoManager.CallEnded -= OnCallEnded;
        }

        private readonly Dictionary<string, ParticipantView> _participantSessionIdToView
            = new Dictionary<string, ParticipantView>();

        [SerializeField]
        private Button _joinBtn;

        [SerializeField]
        private Button _createBtn;

        [SerializeField]
        private Button _leaveBtn;

        [SerializeField]
        private Button _endBtn;

        [SerializeField]
        private TMP_InputField _joinCallIdInput;

        [SerializeField]
        private Transform _participantsContainer;

        [SerializeField]
        private ParticipantView _participantViewPrefab;

        [SerializeField]
        private TMP_Dropdown _microphoneDeviceDropdown;

        [SerializeField]
        private Toggle _microphoneDeviceToggle;

        [SerializeField]
        private TMP_Dropdown _cameraDeviceDropdown;

        [SerializeField]
        private Toggle _cameraDeviceToggle;

        [SerializeField]
        private AudioSource _inputAudioSource;

        [SerializeField]
        private RawImage _localCameraImage;

        [SerializeField]
        private Camera _inputSceneCamera;

        [SerializeField]
        private Toggle _audioRedToggle;

        [SerializeField]
        private Toggle _audioDtxToggle;

        [SerializeField]
        private Transform _beforeCallButtonsContainer;

        [SerializeField]
        private Transform _duringCallButtonsContainer;

        private string _activeMicrophoneDeviceName;

        private WebCamTexture _activeCamera;
        private WebCamDevice _defaultCamera;

        private StreamVideoManager _videoManager;
        private IStreamCall _activeCall;

        private void OnCallStarted(IStreamCall call)
        {
            SetJoinCallId(call.Id);
            SetActiveCall(call);
            
            // Generate participant UI for already present participants
            foreach (var participant in call.Participants)
            {
                AddParticipant(participant);
            }
            
            // Subscribe to participants joining or leaving the call
            call.ParticipantJoined += AddParticipant;
            call.ParticipantLeft += RemoveParticipant;
            
            // Subscribe to the change of the most actively speaking participant
            call.DominantSpeakerChanged += DominantSpeakerChanged;
        }

        private void OnCallEnded()
        {
            _activeCall.ParticipantJoined -= AddParticipant;
            _activeCall.ParticipantLeft -= RemoveParticipant;
            _activeCall.DominantSpeakerChanged -= DominantSpeakerChanged;
            
            SetJoinCallId(string.Empty);
            SetActiveCall(null);
        }

        private async void OnJoinCallButtonClicked()
        {
            try
            {
                if (string.IsNullOrEmpty(_joinCallIdInput.text))
                {
                    Log("`Call ID` is required when trying to join a call", LogType.Error);
                    return;
                }

                _videoManager.Client.SetAudioInputSource(_inputAudioSource);
                _videoManager.Client.SetCameraInputSource(_activeCamera);
                
                // Optional
                _videoManager.Client.SetCameraInputSource(_inputSceneCamera);

                await _videoManager.JoinAsync(_joinCallIdInput.text, create: false);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private async void OnCreateAndJoinCallButtonClicked()
        {
            try
            {
                _videoManager.Client.SetAudioInputSource(_inputAudioSource);
                _videoManager.Client.SetCameraInputSource(_activeCamera);
                _videoManager.Client.SetCameraInputSource(_inputSceneCamera);

                var callId = CreateRandomCallId();
                await _videoManager.JoinAsync(callId, create: true);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private string CreateRandomCallId() => Guid.NewGuid().ToString().Replace("-", "");

        private void Log(string message, LogType type)
        {
            if (type == LogType.Exception)
            {
                throw new NotSupportedException("To log exceptions use " + nameof(Debug.LogException));
            }

            Debug.LogFormat(type, LogOption.None, context: null, format: message);
        }

        private void OnMicrophoneDeviceChanged(int index)
        {
            StopAudioRecording();
            _activeMicrophoneDeviceName = _microphoneDeviceDropdown.options[index].text;
            Debug.Log("Microphone device changed to: " + _activeMicrophoneDeviceName);

            if (_microphoneDeviceToggle.enabled)
            {
                StartAudioRecording();
            }
        }

        private void OnMicrophoneToggled(bool enabled)
        {
            if (enabled)
            {
                StartAudioRecording();
                return;
            }

            StopAudioRecording();
        }

        private void StartAudioRecording()
        {
            if (_inputAudioSource == null)
            {
                Debug.LogError("Input Audio Source is null");
                return;
            }

            //StreamTodo: should the volume be 0 so we never hear input from our own microphone?
            _inputAudioSource.clip
                = Microphone.Start(_activeMicrophoneDeviceName, true, 3, AudioSettings.outputSampleRate);
            _inputAudioSource.loop = true;
            _inputAudioSource.Play();

            Debug.Log("Audio recording started. Device name: " + _activeMicrophoneDeviceName);
        }

        private void StopAudioRecording()
        {
            Microphone.End(_activeMicrophoneDeviceName);
            Debug.Log("Audio recording stopped");
        }

        private void SmartPickDefaultCamera()
        {
            var devices = WebCamTexture.devices;

#if UNITY_STANDALONE_WIN
            //StreamTodo: remove this, "Capture" is our debug camera
            _defaultCamera = devices.FirstOrDefault(d => d.name.Contains("Capture"));

#elif UNITY_ANDROID || UNITY_IOS
            _defaultCamera = devices.FirstOrDefault(d => d.isFrontFacing);

#else
            _defaultCamera = devices.FirstOrDefault();

#endif

            if (string.IsNullOrEmpty(_defaultCamera.name))
            {
                Debug.LogError("Failed to pick default camera device");
                return;
            }

            Debug.Log($"Default Camera: {_defaultCamera.name}");

            if (!string.IsNullOrEmpty(_defaultCamera.name))
            {
                ChangeCamera(_defaultCamera.name);
            }
        }

        //StreamTodo: remove
        private void SmartPickDefaultMicrophone()
        {
            var preferredMicDevices = new[] { "bose", "airpods" };
            var defaultMicrophone = Microphone.devices.FirstOrDefault(d
                => preferredMicDevices.Any(m => d.IndexOf(m, StringComparison.OrdinalIgnoreCase) != -1));

            if (!string.IsNullOrEmpty(defaultMicrophone))
            {
                var index = Array.IndexOf(Microphone.devices, defaultMicrophone);
                if (index == -1)
                {
                    Debug.LogError("Failed to find index of smart picked microphone");
                    return;
                }

                _microphoneDeviceDropdown.value = index;
            }
        }

        private void OnCameraChanged(int optionIndex) => ChangeCamera(_cameraDeviceDropdown.options[optionIndex].text);

        private void ChangeCamera(string deviceName)
        {
            if (_activeCamera != null)
            {
                var prevDevice = _activeCamera.deviceName;

                _activeCamera.Stop();
                _activeCamera.deviceName = deviceName;
                _activeCamera.Play();

                Debug.Log($"Changed active camera from `{prevDevice}` to `{deviceName}`");

                return;
            }

            _activeCamera = new WebCamTexture(deviceName, _senderVideoWidth, _senderVideoHeight, _senderVideoFps);

            _localCameraImage.texture = _activeCamera;

            _activeCamera.Play();

            // if we're during the call - update local participant view with the new camera
            var localParticipant
                = _participantSessionIdToView.Values.FirstOrDefault(p => p.Participant.IsLocalParticipant);
            if (localParticipant != null)
            {
                localParticipant.SetLocalCameraSource(_activeCamera);
            }

            _videoManager.Client.SetCameraInputSource(_activeCamera);
        }

        private void ClearAllParticipants()
        {
            foreach (var (sessionId, view) in _participantSessionIdToView)
            {
                Destroy(view.gameObject);
            }

            _participantSessionIdToView.Clear();
        }
    }
}