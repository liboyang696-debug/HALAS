using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace ExplainableXR
{
    public class UBRParserAndSender : MonoBehaviour
    {
        [Header("Edge Uploader Reference")]
        public EdgeUploader uploader;

        [Header("Real-time Mode Settings")]
        public string sceneId = "default";
        public string userId = "User1";

        [Header("Queue Upload Settings")]
        public float queuePollInterval = 0.1f;
        public int queueWarningThreshold = 50;
        public int queueMaxLength = 100;
        public Logger xrLogger;
        private bool isQueueProcessing = false;

        public static UBRParserAndSender Instance { get; private set; }

        [Serializable]
        public class UBRItem
        {
            public int ActionTypeAndIdBit;
            public string User;
            public string Location;
            public string Timestamp;
            public float Duration;
            public string UserAction;
            public string UserIntent;
            public string ActionTriggerSource;
            public string ActionReferent;
            public string ActionReferentTransform;
            public string ActionContext;
            public string ActionReferentName;
            public string ActionReferentType;
            public string ActionContextType;
        }

        [Serializable]
        public class UBRList
        {
            public List<UBRItem> items;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // Bind logger and start all queue processing coroutines
        public void SetLogger(Logger logger)
        {
            xrLogger = logger;
            if (!isQueueProcessing)
            {
                StartCoroutine(ProcessLogQueueCoroutine());
                StartCoroutine(ProcessContextQueueCoroutine());
                StartCoroutine(ProcessHandQueueCoroutine());
                StartCoroutine(ProcessVoiceQueueCoroutine());
                StartCoroutine(ProcessGLBQueueCoroutine());
                StartCoroutine(ProcessReferentImageQueueCoroutine());
                isQueueProcessing = true;
            }
        }

        public void UploadUBR(UBRItem ubrItem)
        {
            StartCoroutine(UploadUBRCoroutine(ubrItem));
        }

        public void UploadContext(Texture2D colorTexture = null, Texture2D depthTexture = null,
            string cameraJson = null, string colorName = null, string depthName = null)
        {
            StartCoroutine(UploadContextCoroutine(colorTexture, depthTexture, cameraJson, colorName, depthName));
        }

        public void UploadAudio(byte[] audioBytes, string audioName = null)
        {
            StartCoroutine(UploadAudioCoroutine(audioBytes, audioName));
        }

        public void UploadGLB(byte[] glbBytes, string glbName = null)
        {
            StartCoroutine(UploadGLBCoroutine(glbBytes, glbName));
        }

        public void UploadReferentImage(Texture2D colorTexture = null,
            string cameraJson = null, string colorName = null)
        {
            StartCoroutine(UploadReferentImageCoroutine(colorTexture, cameraJson, colorName));
        }

        // Convert UserBehaviorRepresentation to UBRItem
        public static UBRItem CreateFromDescriptor(UserBehaviorRepresentation descriptor)
        {
            return new UBRItem
            {
                ActionTypeAndIdBit = descriptor.ActionTypeAndIdBit,
                User = descriptor.User,
                Location = descriptor.Location,
                Timestamp = descriptor.Timestamp,
                Duration = descriptor.Duration,
                UserAction = descriptor.UserAction,
                UserIntent = descriptor.UserIntent,
                ActionTriggerSource = descriptor.ActionTriggerSource,
                ActionReferent = descriptor.ActionReferent,
                ActionReferentTransform = descriptor.ActionReferentTransform,
                ActionContext = descriptor.ActionContext,
                ActionReferentName = descriptor.ActionReferentName,
                ActionReferentType = descriptor.ActionReferentType,
                ActionContextType = descriptor.ActionContextType
            };
        }

        private IEnumerator UploadUBRCoroutine(UBRItem ubrItem)
        {
            if (uploader == null || !uploader.IsConnected() || ubrItem == null)
                yield break;
            var task = UploadUBRAsync(ubrItem);
            while (!task.IsCompleted)
                yield return null;
        }

        private IEnumerator UploadContextCoroutine(Texture2D colorTexture, Texture2D depthTexture,
            string cameraJson, string colorName, string depthName)
        {
            if (uploader == null || !uploader.IsConnected())
                yield break;
            var task = UploadContextAsync(colorTexture, depthTexture, cameraJson, colorName, depthName);
            while (!task.IsCompleted)
                yield return null;
        }

        private IEnumerator UploadAudioCoroutine(byte[] audioBytes, string audioName)
        {
            if (uploader == null || !uploader.IsConnected())
                yield break;
            var task = UploadAudioAsync(audioBytes, audioName);
            while (!task.IsCompleted)
                yield return null;
        }

        private IEnumerator UploadGLBCoroutine(byte[] glbBytes, string glbName)
        {
            if (uploader == null || !uploader.IsConnected())
                yield break;
            var task = UploadGLBAsync(glbBytes, glbName);
            while (!task.IsCompleted)
                yield return null;
        }

        private IEnumerator UploadReferentImageCoroutine(Texture2D colorTexture,
            string cameraJson, string colorName)
        {
            if (uploader == null || !uploader.IsConnected())
                yield break;
            var task = UploadReferentImageAsync(colorTexture, cameraJson, colorName);
            while (!task.IsCompleted)
                yield return null;
        }

        private async Task UploadUBRAsync(UBRItem ubrItem, string ubrId = null)
        {
            string ubrJson = JsonConvert.SerializeObject(ubrItem, Formatting.Indented);
            await uploader.SendUBRAsync(ubrJson, sceneId, ubrId);
        }

        private async Task UploadContextAsync(Texture2D colorTexture, Texture2D depthTexture,
            string cameraJson, string colorName, string depthName)
        {
            await uploader.SendContextAsync(colorTexture, depthTexture, cameraJson,
                colorName, depthName, sceneId, userId);
        }

        private async Task UploadAudioAsync(byte[] audioBytes, string audioName)
        {
            await uploader.SendAudioAsync(audioBytes, audioName, sceneId, userId);
        }

        private async Task UploadGLBAsync(byte[] glbBytes, string glbName,
            string userId = null, string glbId = null)
        {
            await uploader.SendGLBAsync(glbBytes, glbName, sceneId, userId, glbId);
        }

        private async Task UploadReferentImageAsync(Texture2D colorTexture,
            string cameraJson, string colorName)
        {
            await uploader.SendReferentImageAsync(colorTexture, cameraJson,
                colorName, sceneId, userId);
        }

        // Poll log queue and upload UBR records
        private IEnumerator ProcessLogQueueCoroutine()
        {
            var waitInterval = new WaitForSeconds(queuePollInterval);
            while (true)
            {
                if (xrLogger == null || uploader == null || !uploader.IsConnected())
                {
                    yield return waitInterval;
                    continue;
                }

                UserBehaviorRepresentation logItem;
                bool hasItem = false;

                lock (xrLogger.logQueueLock)
                {
                    int count = xrLogger.logQueue.Count;
                    if (count > queueMaxLength)
                    {
                        for (int i = 0; i < count - queueMaxLength; i++)
                            xrLogger.logQueue.Dequeue();
                    }
                    hasItem = xrLogger.logQueue.Count > 0;
                    logItem = hasItem ? xrLogger.logQueue.Dequeue() : default;
                }

                if (!hasItem)
                {
                    yield return waitInterval;
                    continue;
                }

                int actionId = logItem.ActionTypeAndIdBit & 0xFFFFFF;
                string ubrId = $"UBR_{actionId}_{logItem.Timestamp}";
                var ubrItem = CreateFromDescriptor(logItem);

                var task = UploadUBRAsync(ubrItem, ubrId);
                while (!task.IsCompleted)
                    yield return null;

                yield return null;
            }
        }

        // Poll context queue and upload RGBD data
        private IEnumerator ProcessContextQueueCoroutine()
        {
            var waitInterval = new WaitForSeconds(queuePollInterval);
            while (true)
            {
                if (xrLogger == null || uploader == null || !uploader.IsConnected())
                {
                    yield return waitInterval;
                    continue;
                }

                ContextData contextData;
                bool hasItem = false;

                lock (xrLogger.contextQueueLock)
                {
                    int count = xrLogger.contextQueue.Count;
                    if (count > queueMaxLength)
                    {
                        for (int i = 0; i < count - queueMaxLength; i++)
                        {
                            var discarded = xrLogger.contextQueue.Dequeue();
                            if (discarded.ColorOrHandTexture != null)
                                UnityEngine.Object.Destroy(discarded.ColorOrHandTexture);
                            if (discarded.DepthTexture != null)
                                UnityEngine.Object.Destroy(discarded.DepthTexture);
                        }
                    }
                    hasItem = xrLogger.contextQueue.Count > 0;
                    contextData = hasItem ? xrLogger.contextQueue.Dequeue() : null;
                }

                if (!hasItem)
                {
                    yield return waitInterval;
                    continue;
                }

                _ = UploadContextAsync(
                    contextData.ColorOrHandTexture, contextData.DepthTexture,
                    contextData.CameraJson,
                    contextData.FileName + "_color", contextData.FileName + "_depth"
                ).ContinueWith(task =>
                {
                    if (contextData.ColorOrHandTexture != null)
                        UnityEngine.Object.Destroy(contextData.ColorOrHandTexture);
                    if (contextData.DepthTexture != null)
                        UnityEngine.Object.Destroy(contextData.DepthTexture);
                });

                yield return null;
            }
        }

        // Poll hand gesture queue and upload hand data
        private IEnumerator ProcessHandQueueCoroutine()
        {
            var waitInterval = new WaitForSeconds(queuePollInterval);
            while (true)
            {
                if (xrLogger == null || uploader == null || !uploader.IsConnected())
                {
                    yield return waitInterval;
                    continue;
                }

                ContextData handData;
                bool hasItem = false;

                lock (xrLogger.handQueueLock)
                {
                    int count = xrLogger.handQueue.Count;
                    if (count > queueMaxLength)
                    {
                        for (int i = 0; i < count - queueMaxLength; i++)
                        {
                            var discarded = xrLogger.handQueue.Dequeue();
                            if (discarded.ColorOrHandTexture != null)
                                UnityEngine.Object.Destroy(discarded.ColorOrHandTexture);
                            if (discarded.DepthTexture != null)
                                UnityEngine.Object.Destroy(discarded.DepthTexture);
                        }
                    }
                    hasItem = xrLogger.handQueue.Count > 0;
                    handData = hasItem ? xrLogger.handQueue.Dequeue() : null;
                }

                if (!hasItem)
                {
                    yield return waitInterval;
                    continue;
                }

                _ = UploadContextAsync(
                    handData.ColorOrHandTexture, handData.DepthTexture,
                    handData.CameraJson,
                    handData.FileName + "_hand", handData.FileName + "_depth"
                ).ContinueWith(task =>
                {
                    if (handData.ColorOrHandTexture != null)
                        UnityEngine.Object.Destroy(handData.ColorOrHandTexture);
                    if (handData.DepthTexture != null)
                        UnityEngine.Object.Destroy(handData.DepthTexture);
                });

                yield return null;
            }
        }

        // Poll voice queue and upload audio data
        private IEnumerator ProcessVoiceQueueCoroutine()
        {
            var waitInterval = new WaitForSeconds(queuePollInterval);
            while (true)
            {
                if (xrLogger == null || uploader == null || !uploader.IsConnected())
                {
                    yield return waitInterval;
                    continue;
                }

                VoiceData voiceData;
                bool hasItem = false;

                lock (xrLogger.voiceQueueLock)
                {
                    int count = xrLogger.voiceQueue.Count;
                    if (count > queueMaxLength)
                    {
                        for (int i = 0; i < count - queueMaxLength; i++)
                            xrLogger.voiceQueue.Dequeue();
                    }
                    hasItem = xrLogger.voiceQueue.Count > 0;
                    voiceData = hasItem ? xrLogger.voiceQueue.Dequeue() : null;
                }

                if (!hasItem)
                {
                    yield return waitInterval;
                    continue;
                }

                _ = UploadAudioAsync(voiceData.AudioBytes, voiceData.AudioFilePath);
                yield return null;
            }
        }

        // Poll referent image queue and upload AR Physical referent data
        private IEnumerator ProcessReferentImageQueueCoroutine()
        {
            var waitInterval = new WaitForSeconds(queuePollInterval);
            while (true)
            {
                if (xrLogger == null || uploader == null || !uploader.IsConnected())
                {
                    yield return waitInterval;
                    continue;
                }

                ReferentImageData referentData;
                bool hasItem = false;

                lock (xrLogger.referentImageQueueLock)
                {
                    int count = xrLogger.referentImageQueue.Count;
                    if (count > queueMaxLength)
                    {
                        for (int i = 0; i < count - queueMaxLength; i++)
                        {
                            var discarded = xrLogger.referentImageQueue.Dequeue();
                            if (discarded.ColorTexture != null)
                                UnityEngine.Object.Destroy(discarded.ColorTexture);
                        }
                    }
                    hasItem = xrLogger.referentImageQueue.Count > 0;
                    referentData = hasItem ? xrLogger.referentImageQueue.Dequeue() : null;
                }

                if (!hasItem)
                {
                    yield return waitInterval;
                    continue;
                }

                _ = UploadReferentImageAsync(
                    referentData.ColorTexture,
                    referentData.CameraJson,
                    referentData.FileName + "_arpng"
                ).ContinueWith(task =>
                {
                    if (referentData.ColorTexture != null)
                        UnityEngine.Object.Destroy(referentData.ColorTexture);
                });

                yield return null;
            }
        }

        // Poll GLB queue and upload 3D model data
        private IEnumerator ProcessGLBQueueCoroutine()
        {
            var waitInterval = new WaitForSeconds(queuePollInterval);
            while (true)
            {
                if (xrLogger == null || uploader == null || !uploader.IsConnected())
                {
                    yield return waitInterval;
                    continue;
                }

                GLBData glbData;
                bool hasItem = false;

                lock (xrLogger.glbQueueLock)
                {
                    int count = xrLogger.glbQueue.Count;
                    if (count > queueMaxLength)
                    {
                        for (int i = 0; i < count - queueMaxLength; i++)
                            xrLogger.glbQueue.Dequeue();
                    }
                    hasItem = xrLogger.glbQueue.Count > 0;
                    glbData = hasItem ? xrLogger.glbQueue.Dequeue() : null;
                }

                if (!hasItem)
                {
                    yield return waitInterval;
                    continue;
                }

                if (glbData.GLBBytes == null || glbData.GLBBytes.Length == 0)
                {
                    yield return null;
                    continue;
                }

                string glbId = $"GLB_{glbData.GLBName}_{glbData.Timestamp}";
                var task = UploadGLBAsync(glbData.GLBBytes, glbData.GLBName, userId, glbId);
                while (!task.IsCompleted)
                    yield return null;

                yield return null;
            }
        }
    }
}
