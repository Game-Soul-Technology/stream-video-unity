﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StreamVideo.Core.LowLevelClient;
using UnityEngine;
using Object = UnityEngine.Object;

namespace StreamVideo.Core.DeviceManagers
{
    internal class AudioDeviceManager : DeviceManagerBase<MicrophoneDeviceInfo>, IAudioDeviceManager
    {
        public override MicrophoneDeviceInfo SelectedDevice { get; protected set; }

        public override IEnumerable<MicrophoneDeviceInfo> EnumerateDevices()
        {
            foreach (var device in Microphone.devices)
            {
                yield return new MicrophoneDeviceInfo(device);
            }
        }

        protected override async Task<bool> OnTestDeviceAsync(MicrophoneDeviceInfo device, int msDuration)
        {
            const int sampleRate = 44100;
            var maxRecordingTime = (int)Math.Ceiling(msDuration / 1000f);
            var clip = Microphone.Start(device.Name, true, maxRecordingTime, sampleRate);
            if (clip == null)
            {
                return false;
            }

            await Task.Delay(msDuration);

            //StreamTodo: should we check Microphone.IsRecording? Also some sources add this after Mic.Start() while (!(Microphone.GetPosition(null) > 0)) { }

            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            bool hasData = false;
            foreach (var sample in data)
            {
                if (sample != 0f)
                {
                    hasData = true;
                    break;
                }
            }

            return hasData;
        }

        public void SelectDevice(MicrophoneDeviceInfo device)
        {
            if (!device.IsValid)
            {
                throw new ArgumentException($"{nameof(device)} argument is not valid. The device name is empty.");
            }

            TryStopRecording();

            SelectedDevice = device;
            
            var targetAudioSource = GetOrCreateTargetAudioSource();
            
            targetAudioSource.clip
                = Microphone.Start(SelectedDevice.Name, true, 3, AudioSettings.outputSampleRate);
            targetAudioSource.loop = true;
            
            //StreamTodo: in some cases starting the mic recording before the call was causing the recorded audio being played in speakers
            //I think the reason was that AudioSource was being captured by an AudioListener but once I've joined the call, this disappeared
            //Check if we can have this AudioSource to be ignored by AudioListener's or otherwise mute it when there is not active call session

            if (IsEnabled)
            {
                targetAudioSource.Play();
            }
        }
        
        //StreamTodo: https://docs.unity3d.com/ScriptReference/AudioSource-ignoreListenerPause.html perhaps this should be enabled so that AudioListener doesn't affect recorded audio
        
        internal AudioDeviceManager(RtcSession rtcSession, IStreamVideoClient client)
            : base(rtcSession, client)
        {
        }

        protected override void OnSetEnabled(bool isEnabled)
        {
            if (isEnabled && SelectedDevice.IsValid && !GetOrCreateTargetAudioSource().isPlaying)
            {
                GetOrCreateTargetAudioSource().Play();
            }
            
            RtcSession.TrySetAudioTrackEnabled(isEnabled);
        }

        protected override void OnDisposing()
        {
            TryStopRecording();
            
            if (_targetAudioSourceContainer != null)
            {
                Object.Destroy(_targetAudioSourceContainer);
            }
            
            base.OnDisposing();
        }

        //StreamTodo: wrap all operations on the Microphone devices + monitor for devices list changes
        //We could also allow to smart pick device -> sample each device and check which of them are actually gathering any input

        private AudioSource _targetAudioSource;
        private GameObject _targetAudioSourceContainer;

        private AudioSource GetOrCreateTargetAudioSource()
        {
            if (_targetAudioSource != null)
            {
                return _targetAudioSource;
            }

            _targetAudioSourceContainer = new GameObject()
            {
                name = $"{nameof(AudioDeviceManager)} - Microphone Buffer",
#if !STREAM_DEBUG_ENABLED
                hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave
#endif
            };
            
            _targetAudioSource = _targetAudioSourceContainer.AddComponent<AudioSource>();
            Client.SetAudioInputSource(_targetAudioSource);
            return _targetAudioSource;
        }
        
        private void TryStopRecording()
        {
            if (SelectedDevice.IsValid)
            {
                if (Microphone.IsRecording(SelectedDevice.Name))
                {
                    Microphone.End(SelectedDevice.Name);
                }
            }
        }
    }
}