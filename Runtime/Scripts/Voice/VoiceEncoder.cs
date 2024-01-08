using System;
using Concentus.Enums;
using Concentus.Structs;

namespace ProximityChat
{
    /// <summary>
    /// Encodes queued voice audio samples using Opus.
    /// </summary>
    public class VoiceEncoder
    {
        // Samples queue
        private VoiceDataQueue<short> _voiceSamplesQueue;
        // Encoding
        private OpusEncoder _opusEncoder;
        private byte[] _encodeBuffer;
        private short[] _emptyShorts;
        private readonly int[] FRAME_SIZES = { 2880, 1920, 960, 480, 240, 120 };  // Frame sizes (in sample counts) supported by Opus   
        private int MaxFrameSize => FRAME_SIZES[0];
        private int MinFrameSize => FRAME_SIZES[FRAME_SIZES.Length-1];
        private const int SAMPLE_RATE = 48000; // Opus is built for encoding audio data with a sample rate of 48khz
        private const int SAMPLE_SIZE = sizeof(short);

        /// <summary>
        /// Is there more voice data in the queue left that could be encoded.
        /// </summary>
        public bool HasVoiceLeftToEncode => _voiceSamplesQueue.EnqueuePosition > MinFrameSize;
        /// <summary>
        /// Whether or not the queue is empty.
        /// </summary>
        public bool QueueIsEmpty => _voiceSamplesQueue.EnqueuePosition == 0;
        
        /// <summary>
        /// Initialize the encoder with a queue of voice samples. The encoder
        /// expects the queue to be filled with samples externally, and will
        /// try to encode anything in the queue every frame.
        /// </summary>
        /// <param name="voiceSamplesQueue">Queue of voice samples. Filled externally, likely
        /// by a <see cref="FMODVoiceRecorder"/></param>
        public VoiceEncoder(VoiceDataQueue<short> voiceSamplesQueue)
        {
            _voiceSamplesQueue = voiceSamplesQueue;
            // Initialize the encoder
            _opusEncoder = new OpusEncoder(SAMPLE_RATE, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _encodeBuffer = new byte[MaxFrameSize * SAMPLE_SIZE];
            _emptyShorts = new short[MinFrameSize];
        }
        
        /// <summary>
        /// Encodes as many queued voice audio samples as possible, up to 2880.
        /// </summary>
        /// <param name="forceEncodeWithSilence">Whether or not to force encoding with silence.
        /// When enabled, enough silence will be added to the queued voice audio to meet
        /// the minimum frame size requirement and trigger an encode, ensuring that all audio data
        /// is thereby flushed from the queue.</param>
        /// <returns>Span of an array containing the encoded voice data</returns>
        public Span<byte> GetEncodedVoice(bool forceEncodeWithSilence = false)
        {
            // If forceEncodeWithSilence is enabled and there's still voice data in the queue,
            // but not enough to trigger an encode, append enough silence so it still gets encoded
            if (forceEncodeWithSilence && _voiceSamplesQueue.EnqueuePosition > 0 && _voiceSamplesQueue.EnqueuePosition < MinFrameSize)
            {
                _voiceSamplesQueue.Enqueue(new Span<short>(_emptyShorts).Slice(0, MinFrameSize-_voiceSamplesQueue.EnqueuePosition));
            }
            
            // Find the largest frame size we can use to encode the queued voice samples
            int frameSize = 0;
            for (int i = 0; i < FRAME_SIZES.Length; i++)
            {
                if (_voiceSamplesQueue.EnqueuePosition >= FRAME_SIZES[i])
                {
                    frameSize = FRAME_SIZES[i];
                    break;
                }
            }
            // Return early if there's not enough data to encode
            if (frameSize == 0) return null;
            
            // Encode samples using the determined frame size
            int encodedSize = _opusEncoder.Encode(_voiceSamplesQueue.Data, frameSize, _encodeBuffer, _encodeBuffer.Length);
            _voiceSamplesQueue.Dequeue(frameSize);
            return new Span<byte>(_encodeBuffer, 0, encodedSize);
        }
    }
}
