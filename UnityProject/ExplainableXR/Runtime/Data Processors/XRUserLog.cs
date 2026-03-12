using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using UnityEngine.XR.ARFoundation;
using Unity.VisualScripting;
using Whisper.Utils;

namespace ExplainableXR
{
    using static ExplainableXR.Utility;

    public enum ActionType
    {
        Discrete,
        Continuous
    }

    public enum ReferentType
    {
        None,
        Virtual,
        Physical
    }

    public enum ActionContextType
    {
        None,
        Virtual,
        Physical
    }

    public enum ActionTriggerSource
    {
        XRHMD,
        XRController_L,
        XRController_R,
        XRHand_L,
        XRHand_R,
        ARHandHeld,
        Microphone,
    }

    public enum LogRecordType
    {
        UBRLog,
        Context,
        Hand,
        Voice
    }

    // Context data for Hand/Context queue
    [Serializable]
    public class ContextData
    {
        public string Category; // "Hand" or "Context"
        public Texture2D ColorOrHandTexture;
        public Texture2D DepthTexture;
        public string CameraJson;
        public string FileName;
        public string Timestamp;
    }

    // Voice data for Voice queue
    [Serializable]
    public class VoiceData
    {
        public string AudioFilePath;
        public byte[] AudioBytes;
        public float Duration;
        public string Timestamp;
    }

    // GLB data for GLB queue
    [Serializable]
    public class GLBData
    {
        public string GLBName;
        public byte[] GLBBytes;
        public string Timestamp;
    }

    // Referent image data for AR Physical referent queue
    [Serializable]
    public class ReferentImageData
    {
        public Texture2D ColorTexture;
        public string CameraJson;
        public string FileName;
        public string Timestamp;
    }

    // UBR Format: User Behavior Record
    [Serializable]
    public struct UserBehaviorRepresentation
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

    public class Logger
    {
        private static Logger instance = null;
        private static readonly object lockObject = new object();
        private string dataSavePath = null;
        private List<UserBehaviorRepresentation> logDataList = new List<UserBehaviorRepresentation>();
        private readonly JsonSerializerSettings serializerSettings = new JsonSerializerSettings()
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.Indented
        };
        private string curTime => DateTime.Now.ToString("yyMMdd_HHmmss_fff");
        public ImageRecordManager imageRecordManager = null;
        public ObjectRecordManager objectManager = null;
        public AudioRecordManager audioRecordManager = null;
        private string userID = "";

        // Queues for different data types
        public Queue<UserBehaviorRepresentation> logQueue = new Queue<UserBehaviorRepresentation>();
        public Queue<ContextData> contextQueue = new Queue<ContextData>();
        public Queue<ContextData> handQueue = new Queue<ContextData>();
        public Queue<VoiceData> voiceQueue = new Queue<VoiceData>();
        public Queue<GLBData> glbQueue = new Queue<GLBData>();
        public Queue<ReferentImageData> referentImageQueue = new Queue<ReferentImageData>();

        // Locks for thread-safe queue operations
        public readonly object logQueueLock = new object();
        public readonly object contextQueueLock = new object();
        public readonly object handQueueLock = new object();
        public readonly object voiceQueueLock = new object();
        public readonly object glbQueueLock = new object();
        public readonly object referentImageQueueLock = new object();

        // Constructor for AR environment
        private Logger(MonoBehaviour monoBehaviour, Camera snapshotCam,
            ARCameraManager arCameraManager, AROcclusionManager occlusionManager,
            MicrophoneRecord microphoneManager, string userID)
        {
            var dataSaveTime = DateTime.Now.ToString("yyMMdd_HHmmss");
            var rootDirName = Path.Combine(Application.persistentDataPath, dataSaveTime);
            Directory.CreateDirectory(rootDirName);
            imageRecordManager = new ImageRecordManager(monoBehaviour, snapshotCam, arCameraManager, occlusionManager, rootDirName);
            objectManager = new ObjectRecordManager(rootDirName, glbQueue, glbQueueLock);
            audioRecordManager = new AudioRecordManager(microphoneManager, rootDirName);
            dataSavePath = Path.Combine(rootDirName, $"Log_{dataSaveTime}.json");
            this.userID = userID;
            LogUtils.Level = LogLevel.Warning;
        }

        // Constructor for VR environment (no passthrough or physical camera)
        private Logger(MonoBehaviour monoBehaviour, Camera snapshotCam,
            MicrophoneRecord microphoneManager, string userID)
        {
            var dataSaveTime = DateTime.Now.ToString("yyMMdd_HHmmss");
            var rootDirName = Path.Combine(Application.persistentDataPath, dataSaveTime);
            Directory.CreateDirectory(rootDirName);
            imageRecordManager = new ImageRecordManager(monoBehaviour, snapshotCam, rootDirName);
            objectManager = new ObjectRecordManager(rootDirName, glbQueue, glbQueueLock);
            audioRecordManager = new AudioRecordManager(microphoneManager, rootDirName);
            dataSavePath = Path.Combine(rootDirName, $"Log_{dataSaveTime}.json");
            this.userID = userID;
            LogUtils.Level = LogLevel.Warning;
        }

        // Initialize Logger for AR environment
        public static Logger Initialize(
            MonoBehaviour monoBehaviour, Camera snapshotCam,
            ARCameraManager arCameraManager, AROcclusionManager occlusionManager,
            MicrophoneRecord microphoneManager = null, string userID = null)
        {
            if (instance == null)
            {
                lock (lockObject)
                {
                    if (userID == null || userID.Trim() == "")
                        userID = GetDeviceUniqueID();
                    instance = new Logger(monoBehaviour, snapshotCam, arCameraManager, occlusionManager, microphoneManager, userID);
                }
            }
            return instance;
        }

        // Initialize Logger for VR environment
        public static Logger Initialize(
            MonoBehaviour monoBehaviour, Camera snapshotCam,
            MicrophoneRecord microphoneManager = null, string userID = null)
        {
            if (instance == null)
            {
                lock (lockObject)
                {
                    if (userID == null || userID.Trim() == "")
                        userID = GetDeviceUniqueID();
                    instance = new Logger(monoBehaviour, snapshotCam, microphoneManager, userID);
                }
            }
            MsgPrint($"Initializing Explainable XR Logger with UserID='{userID}'");
            return instance;
        }

        // Discrete action (e.g., XR object select, UI pinch)
        public void LogDiscreteAction(
            string userAction,
            string userActionIntent,
            ActionTriggerSource userActionTriggerSrc,
            Transform userActionTriggerSrcTransform,
            object userActionReferent,
            Transform userActionReferentTransform,
            ReferentType userActionReferentType,
            ActionContextType userActionContextType,
            LogRecordType logRecordType = LogRecordType.UBRLog) => LogAction(
                userAction, userActionIntent, (int)ActionType.Discrete, userActionTriggerSrc,
                userActionTriggerSrcTransform, DISCRETE_ACTION_DURATION_SEC,
                userActionReferent, userActionReferentTransform, userActionReferentType,
                userActionContextType, logRecordType);

        // Continuous action logging
        public static int continuousActionLogId = 2;
        private static Dictionary<int, CachedActionProperties> cachedActions = new();

        private class CachedActionProperties
        {
            public string userAction;
            public string userActionIntent;
            public ActionTriggerSource userActionTriggerSrc;
            public Transform userActionTriggerSrcTransform;
            public object userActionReferent;
            public Transform userActionReferentTransform;
            public ReferentType userActionReferentType;
            public ActionContextType userActionContextType;
            public LogRecordType logRecordType;
        }

        private int GetNewContinuousActionLogId()
        {
            if (continuousActionLogId >= int.MaxValue)
                continuousActionLogId = 2;
            return continuousActionLogId++;
        }

        // First invocation of continuous action logging
        public int LogContinuousActionBegin(
            string userAction,
            string userActionIntent,
            ActionTriggerSource userActionTriggerSrc,
            Transform userActionTriggerSrcTransform,
            object userActionReferent,
            Transform userActionReferentTransform,
            ReferentType userActionReferentType,
            ActionContextType userActionContextType,
            LogRecordType logRecordType = LogRecordType.UBRLog)
        {
            int logId = GetNewContinuousActionLogId();
            cachedActions[logId] = new CachedActionProperties()
            {
                userAction = userAction,
                userActionIntent = userActionIntent,
                userActionTriggerSrc = userActionTriggerSrc,
                userActionTriggerSrcTransform = userActionTriggerSrcTransform,
                userActionReferent = userActionReferent,
                userActionReferentTransform = userActionReferentTransform,
                userActionReferentType = userActionReferentType,
                userActionContextType = userActionContextType,
                logRecordType = logRecordType
            };

            LogAction(
                userAction, userActionIntent, logId, userActionTriggerSrc,
                userActionTriggerSrcTransform, DEFAULT_CONTINUOUS_ACTION_DURATION_SEC,
                userActionReferent, userActionReferentTransform, userActionReferentType,
                userActionContextType, logRecordType);

            if (userActionTriggerSrc == ActionTriggerSource.Microphone)
                audioRecordManager.RecordStart();

            return logId;
        }

        // Subsequent invocations using cached action data
        public int LogContinuousActionContinue(int existingContinuousActionLogId)
        {
            int logId = existingContinuousActionLogId;
            if (!cachedActions.ContainsKey(logId))
            {
                MsgPrintWarning($"ActionID key not present ({existingContinuousActionLogId}). Continuous Log Data not recorded.");
                return 0;
            }

            var data = cachedActions[logId];
            LogAction(
                data.userAction, data.userActionIntent, logId, data.userActionTriggerSrc,
                data.userActionTriggerSrcTransform, DEFAULT_CONTINUOUS_ACTION_DURATION_SEC,
                data.userActionReferent, data.userActionReferentTransform, data.userActionReferentType,
                data.userActionContextType, data.logRecordType);

            return 1;
        }

        // Completion of continuous action logging
        public int LogContinuousActionEnd(int existingContinuousActionLogId)
        {
            int logId = existingContinuousActionLogId;
            if (!cachedActions.ContainsKey(logId))
            {
                MsgPrintWarning($"ActionID key not present ({existingContinuousActionLogId}). Continuous Log Data not recorded.");
                return 0;
            }

            var data = cachedActions[logId];

            if (data.userActionTriggerSrc == ActionTriggerSource.Microphone)
            {
                var recordedAudioFileName = audioRecordManager.RecordStop((wavBytes, duration, fileName) =>
                {
                    if (data.logRecordType == LogRecordType.Voice)
                    {
                        var voiceData = new VoiceData()
                        {
                            AudioFilePath = fileName,
                            AudioBytes = wavBytes,
                            Duration = duration,
                            Timestamp = curTime
                        };
                        lock (voiceQueueLock)
                        {
                            voiceQueue.Enqueue(voiceData);
                        }
                    }
                });
                data.userActionReferent = recordedAudioFileName;
            }

            LogAction(
                data.userAction, data.userActionIntent, logId, data.userActionTriggerSrc,
                data.userActionTriggerSrcTransform, DEFAULT_CONTINUOUS_ACTION_DURATION_SEC,
                data.userActionReferent, data.userActionReferentTransform, data.userActionReferentType,
                data.userActionContextType, data.logRecordType);

            cachedActions.Remove(logId);
            return 1;
        }

        // Pre-defined voice recording log
        public void CustomVoiceRecordLog(byte[] voiceRecordedWAVBytes, float audioDataLength,
            LogRecordType logRecordType = LogRecordType.UBRLog) => LogAction(
                userAction: "Verbal communication",
                userActionIntent: "PostDefined",
                userActionTypeAndIdBit: (int)ActionType.Continuous,
                userActionTriggerSrc: ActionTriggerSource.Microphone,
                userActionTriggerSrcTransform: null,
                userActionDuration: audioDataLength,
                userActionReferent: voiceRecordedWAVBytes,
                userActionReferentTransform: null,
                userActionReferentType: ReferentType.Virtual,
                userActionContextType: ActionContextType.Virtual,
                logRecordType: logRecordType);

        // Generic action logger
        public void LogAction(
            string userAction,
            string userActionIntent,
            int userActionTypeAndIdBit,
            ActionTriggerSource userActionTriggerSrc,
            Transform userActionTriggerSrcTransform,
            float userActionDuration,
            object userActionReferent,
            Transform userActionReferentTransform,
            ReferentType userActionReferentType,
            ActionContextType userActionContextType,
            LogRecordType logRecordType = LogRecordType.UBRLog) => RecordUBRData(
                action: userAction,
                actionIntent: userActionIntent,
                actionTypeAndIdBit: userActionTypeAndIdBit,
                actionTriggerSrc: userActionTriggerSrc,
                actionTransform: userActionTriggerSrcTransform,
                actionDuration: userActionDuration,
                actionReferent: userActionReferent,
                actionReferentTransform: userActionReferentTransform,
                actionReferentType: userActionReferentType,
                actionContextType: userActionContextType,
                logRecordType: logRecordType);

        // Core UBR data recording and dispatch
        public void RecordUBRData(
            string action, string actionIntent,
            int actionTypeAndIdBit, ActionTriggerSource actionTriggerSrc, Transform actionTransform, float actionDuration,
            object actionReferent, Transform actionReferentTransform, ReferentType actionReferentType,
            ActionContextType actionContextType, LogRecordType logRecordType = LogRecordType.UBRLog)
        {
            switch (logRecordType)
            {
                case LogRecordType.UBRLog:
                    var logStruct = new UserBehaviorRepresentation()
                    {
                        ActionTypeAndIdBit = actionTypeAndIdBit,
                        User = userID,
                        Location = (actionTransform != null) ? actionTransform.Unravel() : null,
                        Timestamp = curTime,
                        Duration = actionDuration,
                        UserAction = action.FirstCharacterToUpper(),
                        UserIntent = actionIntent.FirstCharacterToUpper(),
                        ActionTriggerSource = actionTriggerSrc.ToString().FirstCharacterToUpper(),
                        ActionReferent = null,
                        ActionReferentTransform = (actionReferentTransform != null) ? actionReferentTransform.Unravel() : null,
                        ActionReferentType = actionReferentType.ToString(),
                        ActionReferentName = (actionReferent != null) ? (actionReferent as GameObject)?.name ?? "PostDefined" : "PostDefined",
                        ActionContext = null,  // Discontinue use
                        ActionContextType = actionContextType.ToString()
                    };

                    // Link GLB referent if virtual object is provided
                    if (actionReferentType == ReferentType.Virtual && actionReferent != null)
                    {
                        var actionReferentGobj = actionReferent as GameObject;
                        if (actionReferentGobj != null)
                        {
                            GLBData gLBData = new GLBData();
                            string glbFileName = objectManager.SaveObject(actionReferentGobj, gLBData, actionTypeAndIdBit, curTime);
                            logStruct.ActionReferent = glbFileName;
                        }
                    }
                    // AR Physical referent: take screenshot and enqueue image for upload
                    else if (actionReferentType == ReferentType.Physical)
                    {
                        string referentFileName = imageRecordManager.TakePhysicalCameraScreenshot(
                            recordDepth: false,
                            onComplete: (colorTex, depthTex, fileName, cameraJson) =>
                            {
                                var referentData = new ReferentImageData()
                                {
                                    ColorTexture = colorTex,
                                    CameraJson = cameraJson,
                                    FileName = fileName,
                                    Timestamp = curTime
                                };
                                lock (referentImageQueueLock)
                                {
                                    referentImageQueue.Enqueue(referentData);
                                }
                            });
                        logStruct.ActionReferent = referentFileName;
                    }

                    lock (logQueueLock)
                    {
                        logQueue.Enqueue(logStruct);
                    }
                    logDataList.Add(logStruct);
                    break;

                case LogRecordType.Context:
                    if (actionContextType == ActionContextType.Virtual)
                    {
                        imageRecordManager.TakeVirtualCameraScreenshot(actionTriggerSrc, actionReferent,
                            (colorTex, depthTex, fileName, cameraJson) => OnScreenshotComplete(colorTex, depthTex, fileName, cameraJson, "Context"));
                    }
                    else if (actionContextType == ActionContextType.Physical)
                    {
                        imageRecordManager.TakePhysicalCameraScreenshot(recordDepth: true,
                            onComplete: (colorTex, depthTex, fileName, cameraJson) => OnScreenshotComplete(colorTex, depthTex, fileName, cameraJson, "Context"));
                    }
                    break;

                case LogRecordType.Hand:
                    if (actionContextType == ActionContextType.Virtual)
                    {
                        imageRecordManager.TakeVirtualCameraScreenshot(actionTriggerSrc, actionReferent,
                            (colorTex, depthTex, fileName, cameraJson) => OnScreenshotComplete(colorTex, depthTex, fileName, cameraJson, "Hand"));
                    }
                    // AR does not require hand gesture capture
                    break;

                case LogRecordType.Voice:
                    var logStructVoice = new UserBehaviorRepresentation()
                    {
                        ActionTypeAndIdBit = actionTypeAndIdBit,
                        User = userID,
                        Location = (actionTransform != null) ? actionTransform.Unravel() : null,
                        Timestamp = curTime,
                        Duration = actionDuration,
                        UserAction = action.FirstCharacterToUpper(),
                        UserIntent = actionIntent.FirstCharacterToUpper(),
                        ActionTriggerSource = actionTriggerSrc.ToString().FirstCharacterToUpper(),
                        ActionReferent = null,
                        ActionReferentTransform = (actionReferentTransform != null) ? actionReferentTransform.Unravel() : null,
                        ActionReferentType = actionReferentType.ToString(),
                        ActionReferentName = (actionReferent != null) ? (actionReferent as GameObject)?.name ?? "PostDefined" : "PostDefined",
                        ActionContext = null,
                        ActionContextType = actionContextType.ToString()
                    };

                    if (actionReferent is string referentStr)
                        logStructVoice.ActionReferent = referentStr;
                    else if (actionReferent is byte[] wavBytes)
                        logStructVoice.ActionReferent = audioRecordManager.SaveWavBytesToPath(wavBytes);

                    lock (logQueueLock)
                    {
                        logQueue.Enqueue(logStructVoice);
                    }
                    logDataList.Add(logStructVoice);
                    return;
            }
        }

        // Callback after screenshot capture completes
        private void OnScreenshotComplete(Texture2D colorTex, Texture2D depthTex, string fileName, string cameraJson, string category)
        {
            var contextData = new ContextData()
            {
                Category = category,
                ColorOrHandTexture = colorTex,
                DepthTexture = depthTex,
                CameraJson = cameraJson,
                FileName = fileName,
                Timestamp = curTime
            };

            switch (category)
            {
                case "Hand":
                    lock (handQueueLock)
                    {
                        handQueue.Enqueue(contextData);
                    }
                    break;
                case "Context":
                    lock (contextQueueLock)
                    {
                        contextQueue.Enqueue(contextData);
                    }
                    break;
            }
        }

        public void TryResumeLogSession()
        {
            if (File.Exists(dataSavePath))
                LoadXRLogFile();
        }

        public void SaveXRLogFile()
        {
            if (logDataList.Count <= 0)
            {
                MsgPrint("Empty logs. Ignoring save attempt");
                return;
            }
            string json = JsonConvert.SerializeObject(logDataList, serializerSettings);
            File.WriteAllText(dataSavePath, json);
            MsgPrint($"Log file saved at: {dataSavePath}");
        }

        private void LoadXRLogFile()
        {
            string jsonString = File.ReadAllText(dataSavePath);
            logDataList = JsonConvert.DeserializeObject<List<UserBehaviorRepresentation>>(jsonString, serializerSettings);
            MsgPrint("Log file reloaded");
        }

        public List<UserBehaviorRepresentation> LoadXRLogFile(string dataSavePath)
        {
            string jsonString = File.ReadAllText(dataSavePath);
            logDataList = JsonConvert.DeserializeObject<List<UserBehaviorRepresentation>>(jsonString, serializerSettings);
            MsgPrint("Log file reloaded");
            return logDataList.ToList();
        }

        public void OverrideXRLogDataList(List<UserBehaviorRepresentation> newDataList)
        {
            logDataList = newDataList;
            MsgPrint("Old data list overridden");
        }

        // Queue access methods
        public Queue<UserBehaviorRepresentation> GetLogQueue() => logQueue;
        public Queue<ContextData> GetContextQueue() => contextQueue;
        public Queue<ContextData> GetHandQueue() => handQueue;
        public Queue<VoiceData> GetVoiceQueue() => voiceQueue;

        public int GetLogQueueCount() => logQueue.Count;
        public int GetContextQueueCount() => contextQueue.Count;
        public int GetHandQueueCount() => handQueue.Count;
        public int GetVoiceQueueCount() => voiceQueue.Count;

        public UserBehaviorRepresentation DequeueLog() => logQueue.Count > 0 ? logQueue.Dequeue() : default;
        public ContextData DequeueContext() => contextQueue.Count > 0 ? contextQueue.Dequeue() : null;
        public ContextData DequeueHand() => handQueue.Count > 0 ? handQueue.Dequeue() : null;
        public VoiceData DequeueVoice() => voiceQueue.Count > 0 ? voiceQueue.Dequeue() : null;
    }
}
