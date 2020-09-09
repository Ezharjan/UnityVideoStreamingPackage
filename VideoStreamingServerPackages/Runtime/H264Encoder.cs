using System;
using UnityEngine;
using UnityEngine.Profiling;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.Ig.VideoStreaming.Server
{
    public struct H264Encoder : IDisposable
    {
        public void Dispose()
        {
            m_CreateJob.Complete();
            m_EncodeJob.Complete();
            if (m_EncoderPtr.IsCreated)
            {
                H264EncoderPlugin.DestroyEncoder(m_EncoderPtr[0]);
                m_EncoderPtr.Dispose();
            }
        }

        // Start is called before the first frame update
        public JobHandle Start(uint width, uint height, uint frameRate, ref NativeList<byte> spsNalu, ref NativeList<byte> ppsNalu)
        {
            if (!m_CreateJob.Equals(default(JobHandle)))
            {
                Debug.LogWarning("Trying to start the H264Encoder more than once.");
                return default;
            }

            m_EncoderPtr = new NativeArray<IntPtr>(1, Allocator.Persistent);

            var createJob = new CreateEncoderJob
            {
                width = width,
                height = height,
                frameRateNumerator = frameRate,
                encoderPtr = m_EncoderPtr,
                spsNalu = spsNalu,
                ppsNalu = ppsNalu
            };
            m_CreateJob = createJob.Schedule();
            return m_CreateJob;
        }

        public JobHandle Encode(in JobHandle dependsOn, in NativeArray<byte> imageData, ulong timeStampNs, ref NativeList<byte> spsNalu, ref NativeList<byte> ppsNalu, ref NativeList<byte> imageNalu)
        {
            if (!m_CreateJob.IsCompleted)
                return default;
            m_CreateJob.Complete();

            if (!m_EncoderPtr.IsCreated || m_EncoderPtr[0] == IntPtr.Zero || !m_EncodeJob.IsCompleted)
                return default;
            m_EncodeJob.Complete();

            var encodeJob = new EncodeJob
            {
                encoderPtr = m_EncoderPtr[0],
                pixelData = imageData,
                timeStampNs = timeStampNs,
                spsNalu = spsNalu,
                ppsNalu = ppsNalu,
                imageNalu = imageNalu
            };
            m_EncodeJob = encodeJob.Schedule(dependsOn);
            return m_EncodeJob;
        }

        static bool ConsumeBuffer(IntPtr encoderPtr, ref NativeList<byte> spsNalu, ref NativeList<byte> ppsNalu, ref NativeList<byte> imageNalu)
        {
            Profiler.BeginSample("BeginConsumeEncodedBuffer");
            bool success = H264EncoderPlugin.BeginConsumeEncodedBuffer(encoderPtr, out uint bufferSize);
            Profiler.EndSample();
            if (!success)
            {
                Debug.LogWarning("No encoded frame ready.");
                return false;
            }

            imageNalu.ResizeUninitialized((int)bufferSize);

            Profiler.BeginSample("EndConsumeEncodedBuffer");
            ulong bufferTimeStampNs;
            bool isKeyFrame;
            unsafe
            {
                success = H264EncoderPlugin.EndConsumeEncodedBuffer(encoderPtr, (byte*)imageNalu.GetUnsafePtr(), out bufferTimeStampNs, out isKeyFrame);
            }

            Profiler.EndSample();
            if (!success)
            {
                Debug.LogErrorFormat("Could not get {0} bytes encoded buffer", bufferSize);
                return false;
            }

            if (isKeyFrame)
                unsafe
                {
                    //Debug.LogFormat("Produced keyframe at t={0}, size={1}", bufferTimeStampNs, bufferSize);
                    var sz = H264EncoderPlugin.GetSpsNAL(encoderPtr, (byte*)0);
                    spsNalu.ResizeUninitialized((int)sz);
                    H264EncoderPlugin.GetSpsNAL(encoderPtr, (byte*)spsNalu.GetUnsafePtr());

                    sz = H264EncoderPlugin.GetPpsNAL(encoderPtr, (byte*)0);
                    ppsNalu.ResizeUninitialized((int)sz);
                    H264EncoderPlugin.GetPpsNAL(encoderPtr, (byte*)ppsNalu.GetUnsafePtr());
                }

            return true;
        }

        private struct CreateEncoderJob : IJob
        {
            [ReadOnly]
            public uint width;

            [ReadOnly]
            public uint height;

            [ReadOnly]
            public uint frameRateNumerator;

            [WriteOnly]
            public NativeArray<IntPtr> encoderPtr;

            public NativeList<byte> spsNalu;
            public NativeList<byte> ppsNalu;

            public void Execute()
            {
                var encoder = H264EncoderPlugin.CreateEncoder(width, height, frameRateNumerator, 1, 1000000);
                encoderPtr[0] = encoder;
                unsafe
                {
                    uint sz = H264EncoderPlugin.GetSpsNAL(encoder, (byte*)0);
                    spsNalu.ResizeUninitialized((int)sz);
                    H264EncoderPlugin.GetSpsNAL(encoder, (byte*)spsNalu.GetUnsafePtr());

                    sz = H264EncoderPlugin.GetPpsNAL(encoder, (byte*)0);
                    ppsNalu.ResizeUninitialized((int)sz);
                    H264EncoderPlugin.GetPpsNAL(encoder, (byte*)ppsNalu.GetUnsafePtr());
                }
            }
        }

        private struct EncodeJob : IJob
        {
            [ReadOnly]
            [NativeDisableUnsafePtrRestriction]
            public IntPtr encoderPtr;

            [ReadOnly]
            public NativeArray<byte> pixelData;

            [ReadOnly]
            public ulong timeStampNs;

            public NativeList<byte> spsNalu;
            public NativeList<byte> ppsNalu;
            public NativeList<byte> imageNalu;

            public void Execute()
            {
                unsafe
                {
                    Profiler.BeginSample("EncodeFrame");

                    //Debug.Log("Encoding source image with " + pixelData.Length + " bytes");
                    bool success = H264EncoderPlugin.EncodeFrame(encoderPtr, (byte*)pixelData.GetUnsafeReadOnlyPtr(), timeStampNs);
                    Profiler.EndSample();

                    if (!success)
                    {
                        Debug.LogErrorFormat("Error encoding frame at t = {0}", timeStampNs);
                        return;
                    }
                }

                Profiler.BeginSample("ConsumeBuffer");
                ConsumeBuffer(encoderPtr, ref spsNalu, ref ppsNalu, ref imageNalu);
                Profiler.EndSample();
            }
        }

        // Held in native array so we can have it initialized from a job.
        NativeArray<IntPtr> m_EncoderPtr;
        private JobHandle m_CreateJob;
        private JobHandle m_EncodeJob;
    }
}
