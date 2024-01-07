using System;
using Concentus.Enums;
using Concentus.Structs;
using Unity.Netcode;
using UnityEngine;

namespace ProximityChat
{
    /// <summary>
    /// Networks voice audio, recording, encoding and sending it over the network if owner,
    /// otherwise receiving, decoding and playing it as 3D spatial audio.
    /// </summary>
    [RequireComponent(typeof(FMODVoiceEmitter), typeof(FMODVoiceRecorder))]
    public class VoiceNetworker : NetworkBehaviour
    {
        [Header("Debug")]
        [SerializeField] private bool _debugVoice;

        // Record/playback
        private FMODVoiceRecorder _voiceRecorder;
        private FMODVoiceEmitter _voiceEmitter;

        // Encoding/decoding
        private VoiceDataQueue<short> _voiceSamplesQueue;
        private OpusEncoder _opusEncoder;
        private OpusDecoder _opusDecoder;
        private byte[] _encodeBuffer;
        private short[] _decodeBuffer;
        private short[] _emptyShorts;
        private readonly int[] FRAME_SIZES = { 2880, 1920, 960, 480, 240, 120 };
        private int MaxFrameSize => FRAME_SIZES[0];
        private int MinFrameSize => FRAME_SIZES[FRAME_SIZES.Length-1];

        void Start()
        {
            _voiceRecorder = GetComponent<FMODVoiceRecorder>();
            _voiceEmitter = GetComponent<FMODVoiceEmitter>();

            // Owner should record voice and encode it
            if (IsOwner)
            {
                // Disable voice emitter
                _voiceEmitter.enabled = _debugVoice;
                // Initialize voice recorder
                _voiceRecorder.Init(0, VoiceFormat.PCM16Samples);
                _voiceSamplesQueue = new VoiceDataQueue<short>(48000);
                // Initialize Opus encoder
                _opusEncoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                _encodeBuffer = new byte[MaxFrameSize * sizeof(short)];
                _emptyShorts = new short[MinFrameSize];
                // Add audio to queue whenever it's recorded
                _voiceRecorder.PingVoiceRecorded += delegate()
                {
                    _voiceSamplesQueue.Enqueue(_voiceRecorder.GetVoiceSamples());
                };
            }
            // Non-owners should receive encoded voice,
            // decode it and play it as audio
            if (!IsOwner || _debugVoice)
            {
                // Disable voice recorder
                _voiceRecorder.enabled = _debugVoice;
                // Initialize voice emitter
                _voiceEmitter.Init(48000, 1, VoiceFormat.PCM16Samples);
                // Initialize Opus decoding
                _opusDecoder = new OpusDecoder(48000, 1);
                _decodeBuffer = new short[MaxFrameSize];
            }
        }

        [ServerRpc]
        public void SendEncodedVoiceServerRpc(byte[] encodedVoiceData)
        {
            SendEncodedVoiceClientRpc(encodedVoiceData);   
        }

        [ClientRpc]
        public void SendEncodedVoiceClientRpc(byte[] encodedVoiceData)
        {
            if (!IsOwner || _debugVoice)
            {
                Span<short> decodedVoiceSamples = DecodeVoiceSamples(encodedVoiceData);
                _voiceEmitter.EnqueueSamplesForPlayback(decodedVoiceSamples);
            }
        }

        /// <summary>
        /// Starts recording and sending voice data over the network.
        /// </summary>
        public void StartRecording()
        {
            if (!IsOwner) return;
            _voiceRecorder.StartRecording();
        }
        
        /// <summary>
        /// Stops recording and sending voice data over the network.
        /// </summary>
        public void StopRecording()
        {
            if (!IsOwner) return;
            _voiceRecorder.StopRecording();
        }
        
        /// <summary>
        /// Encodes as many queued voice audio samples as possible.
        /// </summary>
        /// <param name="voiceSamplesQueue">Voice audio samples queue</param>
        /// <returns>Span of encoded voice data array</returns>
        private Span<byte> EncodeVoiceSamples(VoiceDataQueue<short> voiceSamplesQueue)
        {
            // Find the largest frame size we can use to encode the queued voice samples
            int frameSize = 0;
            for (int i = 0; i < FRAME_SIZES.Length; i++)
            {
                if (voiceSamplesQueue.Length >= FRAME_SIZES[i])
                {
                    frameSize = FRAME_SIZES[i];
                    break;
                }
            }
            // Return early if there's nothing to encode
            if (frameSize == 0) return null;
            
            // Encode samples using the determined frame size
            int encodedSize = _opusEncoder.Encode(voiceSamplesQueue.Data, frameSize, _encodeBuffer, _encodeBuffer.Length);
            voiceSamplesQueue.Dequeue(frameSize);
            return new Span<byte>(_encodeBuffer, 0, encodedSize);
        }
        
        /// <summary>
        /// Decodes encoded voice audio to decompressed PCM samples.
        /// </summary>
        /// <param name="encodedVoiceData">Encoded voice data returned from <see cref="EncodeVoiceSamples"/>/></param>
        /// <returns>Span of array to which audio was encoded</returns>
        private Span<short> DecodeVoiceSamples(Span<byte> encodedVoiceData)
        {
            // Get the frame size from the encoded audio data
            int frameSize = OpusPacketInfo.GetNumSamples(encodedVoiceData, 0, encodedVoiceData.Length, _opusDecoder.SampleRate);
            // Decode the audio data to voice samples
            int decodedSize = _opusDecoder.Decode(encodedVoiceData, _decodeBuffer, frameSize);
            return new Span<short> (_decodeBuffer, 0, decodedSize);
        }

        void LateUpdate()
        {
            if (IsOwner)
            {
                // If we're no longer recording and there's still voice data in the queue,
                // but not enough to trigger an encode, we want append enough silence
                // to ensure it will get encoded
                if (!_voiceRecorder.IsRecording && _voiceSamplesQueue.Length > 0 && _voiceSamplesQueue.Length < MinFrameSize)
                {
                    _voiceSamplesQueue.Enqueue(new Span<short>(_emptyShorts).Slice(0, MinFrameSize-_voiceSamplesQueue.Length));
                }
                // Encode what's currently in the queue
                if (_voiceSamplesQueue.Length > 0)
                {
                    Span<byte> encodedData = EncodeVoiceSamples(_voiceSamplesQueue);
                    // Send encoded voice to everyone
                    if (encodedData != null)
                    {
                        SendEncodedVoiceServerRpc(encodedData.ToArray()); // TODO: Any way to avoid this allocation?
                    }
                }
            }
        }
    }
}
