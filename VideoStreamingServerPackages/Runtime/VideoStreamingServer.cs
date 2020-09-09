using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Unity.Ig.VideoStreaming.Server;

namespace Unity.Ig.VideoStreaming.Server
{
    public class VideoStreamingServer : MonoBehaviour
    {
        [SerializeField]
        int m_Port = 8554;
        public int Port { set { m_Port = value; } }

        [SerializeField]
        string m_Username = "user"; // or use NUL if there is no username
        [SerializeField]
        string m_Password = "password"; // or use NUL if there is no password

        public void SetCredentials(string username, string password)
        {
            m_Username = username;
            m_Password = password;
        }

        [SerializeField]
        RenderTexture m_SourceTexture;
        public RenderTexture SourceTexture  { set { m_SourceTexture = value; } }

        [SerializeField]
        int m_Framerate = 30;
        public int Framerate { set { m_Framerate = value; } }

        RtspServer m_Server;
        H264Encoder m_Encoder;
        const float m_StartTime = 3.0F;
        int m_LastFrameIndex = -1;
        bool m_RtspServerActive = false;
        RenderTexture m_NV12Texture;
        Material m_RGBToNV12Material;

        struct H264EncoderForStartup : IH264Encoder
        {
            public byte[] spsNalu;
            public byte[] ppsNalu;

            public void CompressFrame(NativeArray<byte> data, ref ArraySegment<byte> compressedData, out bool isKeyFrame)
            {
                throw new NotImplementedException();
            }

            public bool ExpectsYUV()
            {
                return false;
            }

            public byte[] GetRawPPS()
            {
                return ppsNalu;
            }

            public byte[] GetRawSPS()
            {
                return spsNalu;
            }
        }

        // Start is called before the first frame update
        void Start()
        {
            if (m_SourceTexture == null)
                return;

            try
            {
                m_Server = new RtspServer(m_Port, m_Username, m_Password);
                m_Encoder = new H264Encoder();
                NativeList<byte> spsNalu = new NativeList<byte>(Allocator.TempJob);
                NativeList<byte> ppsNalu = new NativeList<byte>(Allocator.TempJob);
                var startJob = m_Encoder.Start((uint)m_SourceTexture.width, (uint)m_SourceTexture.height,
                    (uint)m_Framerate, ref spsNalu, ref ppsNalu);
                m_JobQueue.Enqueue(new JobItem { job = startJob, spsNalu = spsNalu, ppsNalu = ppsNalu });

                if (String.IsNullOrEmpty(m_Username) != String.IsNullOrEmpty(m_Password))
                    Debug.LogError("Both username and password should be specified, not just one of them.");
                else if (!String.IsNullOrEmpty(m_Username) && !String.IsNullOrEmpty(m_Password))
                    Debug.Log($"Watch the output on rtsp://{m_Username}:{m_Password}@{GetMyIPAddress()}:{m_Port}");
                else
                    Debug.Log($"Watch the output on rtsp://{GetMyIPAddress()}:{m_Port}");

                var shaderName = "Video/RGBToNV12";
                var rgbToNV12Shader = Shader.Find(shaderName);
                if (rgbToNV12Shader == null)
                    Debug.LogError("Could not find shader " + shaderName);
                m_RGBToNV12Material = new Material(rgbToNV12Shader);
                m_NV12Texture = new RenderTexture(m_SourceTexture.width, m_SourceTexture.height * 3 / 2, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
                m_NV12Texture.Create();
            }
            catch (Exception e)
            {
                Debug.LogError("Error: Could not start server: " + e);
            }
        }

        void OnDestroy()
        {
            m_Server?.StopListen();
            m_Server?.Dispose();
            while (m_JobQueue.Count > 0)
                CompleteJobs();
            m_Server = null;
            m_Encoder.Dispose();
            if (m_NV12Texture != null)
            {
                Destroy(m_NV12Texture);
                m_NV12Texture = null;
            }

            if (m_RGBToNV12Material != null)
            {
                Destroy(m_RGBToNV12Material);
                m_RGBToNV12Material = null;
            }
        }

        struct JobItem
        {
            public AsyncGPUReadbackRequest req;
            public NativeList<byte> spsNalu;
            public NativeList<byte> ppsNalu;
            public NativeList<byte> imageNalu;
            public JobHandle job;
            public ulong timeStampNs;

            public bool CompleteIfPossible()
            {
                if (!job.IsCompleted)
                    return false;
                job.Complete();
                return true;
            }

            public void DisposeLists()
            {
                if (spsNalu.IsCreated)
                    spsNalu.Dispose();
                if (ppsNalu.IsCreated)
                    ppsNalu.Dispose();
                if (imageNalu.IsCreated)
                    imageNalu.Dispose();
            }
        };

        Queue<JobItem> m_JobQueue = new Queue<JobItem>();

        void Update()
        {
            CompleteJobs();
            if (m_Framerate == 0 || m_SourceTexture == null)
                return;

            var curTime = Time.timeSinceLevelLoad;
            if (curTime < m_StartTime)
                return;

            var elapsedTime = curTime - m_StartTime;
            var frameIndex = (int)(elapsedTime * m_Framerate);
            if (frameIndex == m_LastFrameIndex)
                return;

            m_RtspServerActive = m_Server.RefreshConnectionList();
            if (!m_RtspServerActive)
                return;

            ulong timeStampNs = (ulong)(elapsedTime * 1000000000);
            m_LastFrameIndex = frameIndex;
            var readbackStart = Time.realtimeSinceStartup;

            Graphics.Blit(m_SourceTexture, m_NV12Texture, m_RGBToNV12Material);

            Profiler.BeginSample("Schedule AsyncGPUReadback");
            AsyncGPUReadback.Request(m_NV12Texture, 0, (request) =>
            {
                //Debug.Log($"Readback took {Time.realtimeSinceStartup - readbackStart} s");
                EncodeImage(request, timeStampNs);
            });
            Profiler.EndSample();
        }

        void EncodeImage(AsyncGPUReadbackRequest req, ulong timeStampNs)
        {
            if (m_Server == null)
                return;
            var spsNalu = new NativeList<byte>(Allocator.TempJob);
            var ppsNalu = new NativeList<byte>(Allocator.TempJob);
            var imageNalu = new NativeList<byte>(Allocator.TempJob);
            JobHandle prevJob = m_JobQueue.Count > 0 ? m_JobQueue.Peek().job : default(JobHandle);
            var encodeJob = m_Encoder.Encode(prevJob, req.GetData<byte>(), timeStampNs, ref spsNalu, ref ppsNalu, ref imageNalu);

            //Debug.Log("Encode image at t=" + timeStampNs);
            if (encodeJob.Equals(default(JobHandle)))
            {
                spsNalu.Dispose();
                ppsNalu.Dispose();
                imageNalu.Dispose();
                return;
            }

            m_JobQueue.Enqueue(new JobItem { job = encodeJob, req = req, spsNalu = spsNalu, ppsNalu = ppsNalu, imageNalu = imageNalu, timeStampNs = timeStampNs });
        }

        private void CompleteJobs()
        {
            while (m_JobQueue.Count > 0)
            {
                if (!m_JobQueue.Peek().CompleteIfPossible())
                    break;
                var jobItem = m_JobQueue.Dequeue();
                if (!jobItem.imageNalu.IsCreated)

                    // Nalu without image denotes the initial job, which just returns the sps/pps. Getting this info makes us ready to open
                    // the RTSP TCP end point.
                    m_Server.StartListen(new H264EncoderForStartup()
                    {
                        spsNalu = jobItem.spsNalu.ToArray(),
                        ppsNalu = jobItem.ppsNalu.ToArray()
                    });
                else
                {
                    //Debug.LogFormat("Sending image NALU with {0} bytes", jobItem.imageNalu.Length);
                    m_Server.SendNALUs(jobItem.timeStampNs, in jobItem.spsNalu, in jobItem.ppsNalu, in jobItem.imageNalu);
                }

                jobItem.DisposeLists();
            }
        }

        string GetMyIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ipAddress = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
            return ipAddress.ToString();
        }
    }
}
