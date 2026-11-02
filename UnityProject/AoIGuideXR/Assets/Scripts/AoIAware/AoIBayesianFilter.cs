using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using ExplainableXR;

public enum UserActionType { Gesture, EyeTracking, Voice, Pose }

public enum ActionEvidenceType
{
    PoseMotion, TargetGaze, GrabStart, GrabHold, Release,
    VoiceActive, ARSelect, MarkerPlace, MemoCreate
}

public enum IntentType
{
    Unknown = -1,
    Interaction = 0,
    Movement = 1,
    EyeTracking = 2,
    Communication = 3
}

public enum BehaviorEventPhase { Begin, Continue, End, Discrete }
public enum BehaviorTemporalType { Instant, Continuous, Hybrid }
public enum EvidenceValue { Unknown, False, True }
public enum ReferentObservation { None, Object, Target }
public enum TriggerModality { Unknown, Controller, Hand, Eye, Voice, HMD, Handheld }
public enum CurrentBehaviorState { None, Active, Attached, TargetFocused, Navigating, Speaking }

[Flags]
public enum AoINovelEvidence
{
    None = 0,
    TargetRelevant = 1 << 0,
    TargetIrrelevant = 1 << 1,
    AttachConfirmed = 1 << 2,
    MotionStarted = 1 << 3,
    Sustained250Ms = 1 << 4,
    Sustained1000Ms = 1 << 5,
    Sustained3000Ms = 1 << 6,
    EndedBefore250Ms = 1 << 7,
    SameTaskBehaviorInWindow = 1 << 8,
    NoTaskBehaviorInWindow = 1 << 9,
    RepeatedInWindow = 1 << 10,
    SparseInWindow = 1 << 11
}

/// <summary>
/// One causal observation of a complete behavior. Continue observations describe only
/// the interval since the preceding observation; they are not complete behavior summaries.
/// Ground-truth labels are deliberately absent from this inference object.
/// </summary>
public sealed class ObservedAction
{
    public ActionEvidenceType evidenceType;
    public string actionDescription;
    public GameObject targetObject;
    public float timestamp;
    public int payloadBytes;
    public string behaviorId;
    public BehaviorEventPhase phase;
    public BehaviorTemporalType temporalType;
    public IntentType intentType;
    public TriggerModality modality;
    public CurrentBehaviorState currentState;

    public int sequenceNumber = -1;
    public float elapsedSeconds;
    public float intervalSeconds;
    public float translationMeters;
    public float rotationDegrees;
    public float linearSpeed;
    public float angularSpeed;
    public EvidenceValue targetRelevant;
    public EvidenceValue attached;
    public EvidenceValue motionObserved;
    public EvidenceValue crossModalSupport;

    public ObservedAction(ActionEvidenceType evidence, string description,
        GameObject target = null, int payloadBytes = 0, string behaviorId = null,
        BehaviorEventPhase phase = BehaviorEventPhase.Discrete,
        TriggerModality modality = TriggerModality.Unknown,
        CurrentBehaviorState currentState = CurrentBehaviorState.None,
        IntentType intentType = IntentType.Unknown,
        BehaviorTemporalType temporalType = BehaviorTemporalType.Continuous)
    {
        evidenceType = evidence;
        actionDescription = description ?? string.Empty;
        targetObject = target;
        timestamp = Time.unscaledTime;
        this.payloadBytes = Mathf.Max(0, payloadBytes);
        this.behaviorId = string.IsNullOrEmpty(behaviorId)
            ? "event-" + Guid.NewGuid().ToString("N") : behaviorId;
        this.phase = phase;
        this.temporalType = phase == BehaviorEventPhase.Discrete
            ? BehaviorTemporalType.Instant : temporalType;
        this.intentType = intentType == IntentType.Unknown
            ? AoIBayesianFilter.IntentForEvidence(evidence) : intentType;
        this.modality = modality == TriggerModality.Unknown
            ? InferDefaultModality(evidence) : modality;
        this.currentState = currentState == CurrentBehaviorState.None
            ? InferDefaultState(evidence, phase) : currentState;
        targetRelevant = EvidenceValue.Unknown;
        attached = this.currentState == CurrentBehaviorState.Attached
            ? EvidenceValue.True : EvidenceValue.Unknown;
        motionObserved = EvidenceValue.Unknown;
        crossModalSupport = EvidenceValue.Unknown;
    }

    public ObservedAction(UserActionType action, IntentType unusedGroundTruth,
        string description, GameObject target = null)
        : this(MapLegacyAction(action), description, target) { }

    private static ActionEvidenceType MapLegacyAction(UserActionType action)
    {
        switch (action)
        {
            case UserActionType.Gesture: return ActionEvidenceType.GrabStart;
            case UserActionType.EyeTracking: return ActionEvidenceType.TargetGaze;
            case UserActionType.Voice: return ActionEvidenceType.VoiceActive;
            default: return ActionEvidenceType.PoseMotion;
        }
    }

    private static TriggerModality InferDefaultModality(ActionEvidenceType evidence)
    {
        switch (evidence)
        {
            case ActionEvidenceType.TargetGaze: return TriggerModality.Eye;
            case ActionEvidenceType.VoiceActive: return TriggerModality.Voice;
            case ActionEvidenceType.PoseMotion: return TriggerModality.HMD;
            default: return TriggerModality.Controller;
        }
    }

    private static CurrentBehaviorState InferDefaultState(
        ActionEvidenceType evidence, BehaviorEventPhase phase)
    {
        switch (evidence)
        {
            case ActionEvidenceType.GrabStart:
            case ActionEvidenceType.GrabHold:
                return CurrentBehaviorState.Attached;
            case ActionEvidenceType.Release:
                return CurrentBehaviorState.Active;
            case ActionEvidenceType.TargetGaze:
                return CurrentBehaviorState.TargetFocused;
            case ActionEvidenceType.PoseMotion:
                return CurrentBehaviorState.Navigating;
            case ActionEvidenceType.VoiceActive:
                return CurrentBehaviorState.Speaking;
            default:
                return CurrentBehaviorState.Active;
        }
    }
}

public sealed class AoIDecision
{
    public bool shouldUpload;
    public bool wouldUpload;
    public bool crossedThreshold;
    public bool provisional;
    public bool mustClose;
    public bool confirmed;
    public bool newlyConfirmed;
    public bool rejected;
    public bool sendCancel;
    public bool allowExpensiveUploads;
    public bool protocolViolation;
    public float aoiScore;
    public float[] posterior;
    public string behaviorId;
    public int sequenceNumber;
    public BehaviorEventPhase phase;
    public float decisionLatencyMs;
    public AoINovelEvidence evidenceApplied;
    public int activeAoIBehaviorCount;
    public int windowSampleCount;
}

/// <summary>
/// Compatibility hook retained for existing recorders. The model was trained
/// without lifecycle metadata, so no Phase/AoIState/fragment fields are sent.
/// </summary>
public static class AoIUBRMetadataBridge
{
    public static void Register(int actionId, ObservedAction action,
        AoIDecision decision, BehaviorEventPhase? transmittedPhase = null)
    {
        // Intentionally empty: every logger record is one independent UBR.
    }
}

[Serializable]
public class AoIFilterConfig
{
    public string deviceMode = "Auto";
    public bool calibrationMode;
    public bool collectExperimentData = true;
    public bool useProvisionalBegin = true;
    // The filter uses one threshold. The tau* fields remain serialized so
    // existing scenes and CSV readers do not break; validation makes them equal.
    public float tauStart = 0.67f;
    public float tauKeep = 0.67f;
    public float tauStop = 0.67f;
    public float tauLow = 0.67f;
    public float tauHigh = 0.67f;
    public float aoiThreshold = 0.67f;
    public float continuityThresholdSeconds = 0.25f;
    public float behaviorWindowSeconds = 0.5f;

    public string participantId = "P000";
    public string sessionId = "Session000";
    public string taskId = "TaskUnknown";
    public string trialId = "Trial000";

    public bool aoiInteraction = true;
    public bool aoiMovement;
    public bool aoiEyeTracking;
    public bool aoiCommunication;

    // Interaction, Movement, EyeTracking, Communication.
    public float[] meaningfulPriorByIntent = { 0.40625f, 0.0025641f, 0.1666667f, 0.5f };

    // Category x relevance state (NonAoI, AoI) x one-time evidence token.
    // A value is P(token occurs at least once in one complete behavior | state, category).
    // Missing values default to 0.5 until offline calibration supplies them.
    public float[] evidenceTokenLikelihoodByIntentAndState = new float[96];


}

/// <summary>
/// Non-blocking multi-label Bayesian controller. Every active behaviorId owns an
/// independent static hidden state: whether that complete behavior belongs to the
/// current Analysis-of-Interest set. Unity may check every 10-50 ms, but a likelihood
/// is applied only when a new causal fact first appears. Several simultaneous
/// behaviors may therefore cross the same single threshold and enter the AoI set.
/// </summary>
public class AoIBayesianFilter : MonoBehaviour
{
    public const int StateCount = 2;
    public const int IntentCount = 4;
    public const int EventTypeCount = 9;
    public const int PhaseCount = 4;
    public const int EvidenceValueCount = 3;
    public const int DurationBucketCount = 4;
    public const int ModalityCount = 7;
    public const int CurrentStateObservationCount = 6;
    public const int ContinuityValueCount = 2;
    public const int EvidenceTokenCount = 12;

    private const float MinProbability = 0.000001f;
    private const string ConfigFileName = "AoIFilterConfig.json";
    private const string CalibratedConfigFileName = "AoIFilterConfig.calibrated.json";

    private sealed class BehaviorFilterState
    {
        public string behaviorId;
        public IntentType intentType;
        public BehaviorTemporalType temporalType;
        public float[] posterior;
        public float startTimestamp;
        public float lastTimestamp;
        public int nextSequence;
        public bool edgeOpened;
        public bool confirmed;
        public bool rejected;
        public bool targetEvidenceApplied;
        public AoINovelEvidence appliedEvidence;
        public bool targetRelevantObserved;
        public readonly Queue<float> recentObservationTimes = new Queue<float>();
    }

    public float aoiThreshold = 0.67f;
    public float CurrentAoIScore { get; private set; }
    public bool CalibrationMode { get; private set; }
    public bool FilteringEnabled { get; private set; } = true;
    public string DeviceMode { get; private set; }
    public float WindowSeconds { get { return config == null ? 0.5f : config.behaviorWindowSeconds; } }
    public int ActiveBehaviorCount { get { return activeBehaviors.Count; } }
    public bool HasConfirmedBehavior
    {
        get
        {
            foreach (BehaviorFilterState state in activeBehaviors.Values)
                if (state.confirmed && !state.rejected) return true;
            return false;
        }
    }

    public event Action<ObservedAction, AoIDecision> EvidenceEvaluated;

    private AoIFilterConfig config;
    private readonly bool[] relevantBehaviors = new bool[IntentCount];
    private readonly bool[] activeGroundTruth = new bool[IntentCount];
    private readonly Dictionary<string, BehaviorFilterState> activeBehaviors =
        new Dictionary<string, BehaviorFilterState>();
    private bool groundTruthAvailable;
    private bool requireRelevantTargetForInteractionGroundTruth;
    private bool requireRelevantTargetForGazeGroundTruth;
    private bool requireContextForMovementGroundTruth;
    private bool requireContextForGazeGroundTruth;
    private bool requireContextForCommunicationGroundTruth;
    private int eventIndex;
    private StreamWriter experimentWriter;
    private float lastRelevantInteractionTimestamp = float.NegativeInfinity;

    private void Awake()
    {
        ApplyConfig(LoadConfig());
        OpenExperimentLog();
    }

    private AoIFilterConfig LoadConfig()
    {
        string calibratedPath = Path.Combine(Application.persistentDataPath,
            "AoIExperiments", CalibratedConfigFileName);
        string packagedCalibratedPath = Path.Combine(Application.streamingAssetsPath,
            CalibratedConfigFileName);
        string builtInPath = Path.Combine(Application.streamingAssetsPath, ConfigFileName);
        string[] paths = { calibratedPath, packagedCalibratedPath, builtInPath };
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                AoIFilterConfig loaded = JsonUtility.FromJson<AoIFilterConfig>(File.ReadAllText(path));
                if (loaded != null
                    && loaded.meaningfulPriorByIntent != null
                    && loaded.meaningfulPriorByIntent.Length == IntentCount
                    && loaded.evidenceTokenLikelihoodByIntentAndState != null
                    && loaded.evidenceTokenLikelihoodByIntentAndState.Length
                        == IntentCount * StateCount * EvidenceTokenCount)
                    return loaded;
                Debug.LogWarning("Ignoring AoI config with invalid parameter dimensions: " + path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("AoI config load failed at " + path + ": " + e.Message);
            }
        }
        return new AoIFilterConfig();
    }

    private void ApplyConfig(AoIFilterConfig value)
    {
        config = value ?? new AoIFilterConfig();
        ValidateAndNormalizeConfig(config);
        aoiThreshold = config.aoiThreshold;
        CalibrationMode = config.calibrationMode;
        relevantBehaviors[0] = config.aoiInteraction;
        relevantBehaviors[1] = config.aoiMovement;
        relevantBehaviors[2] = config.aoiEyeTracking;
        relevantBehaviors[3] = config.aoiCommunication;
        DeviceMode = ResolveDeviceMode(config.deviceMode);
        CurrentAoIScore = 0f;
    }

    public static void ValidateAndNormalizeConfig(AoIFilterConfig value)
    {
        if (value == null) return;
        value.meaningfulPriorByIntent = EnsureProbabilityArray(
            value.meaningfulPriorByIntent, IntentCount, 0.5f);
        value.evidenceTokenLikelihoodByIntentAndState = EnsureProbabilityArray(
            value.evidenceTokenLikelihoodByIntentAndState,
            IntentCount * StateCount * EvidenceTokenCount, 0.5f);
        value.aoiThreshold = Mathf.Clamp01(value.aoiThreshold);
        // A category prior is not behavioral evidence. Enforce the method's
        // invariant so no behavior can be confirmed immediately by its prior.
        float largestPrior = 0f;
        for (int i = 0; i < value.meaningfulPriorByIntent.Length; i++)
            largestPrior = Mathf.Max(largestPrior,
                value.meaningfulPriorByIntent[i]);
        value.aoiThreshold = Mathf.Max(value.aoiThreshold,
            Mathf.Min(0.999f, largestPrior + 0.001f));
        value.continuityThresholdSeconds = Mathf.Max(0.01f,
            value.continuityThresholdSeconds);
        value.behaviorWindowSeconds = Mathf.Max(0.1f, value.behaviorWindowSeconds);
        value.tauStart = value.aoiThreshold;
        value.tauKeep = value.aoiThreshold;
        value.tauStop = value.aoiThreshold;
        value.tauLow = value.aoiThreshold;
        value.tauHigh = value.aoiThreshold;
    }

    private static float[] EnsureProbabilityArray(float[] values, int length, float fallback)
    {
        float[] result = new float[length];
        for (int i = 0; i < length; i++)
        {
            float raw = values != null && i < values.Length ? values[i] : fallback;
            if (raw <= 0f) raw = fallback;
            result[i] = Mathf.Clamp(raw, MinProbability, 1f - MinProbability);
        }
        return result;
    }

    private static void NormalizeInPlace(float[] values, int offset, int count)
    {
        float total = 0f;
        for (int i = 0; i < count; i++)
        {
            values[offset + i] = Mathf.Max(MinProbability, values[offset + i]);
            total += values[offset + i];
        }
        for (int i = 0; i < count; i++) values[offset + i] /= total;
    }

    private static string ResolveDeviceMode(string configured)
    {
        string mode = string.IsNullOrEmpty(configured) ? "Auto" : configured.Trim();
        if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            mode = SceneManager.GetActiveScene().name.IndexOf(
                "AR", StringComparison.OrdinalIgnoreCase) >= 0 ? "AR" : "VR";
        return mode.Equals("AR", StringComparison.OrdinalIgnoreCase) ? "AR" : "VR";
    }

    public AoIDecision ObserveEvidence(ObservedAction action)
    {
        if (action == null) return new AoIDecision();
        if (!FilteringEnabled)
        {
            CurrentAoIScore = 1f;
            return new AoIDecision
            {
                shouldUpload = true,
                wouldUpload = true,
                crossedThreshold = false,
                provisional = false,
                mustClose = action.phase == BehaviorEventPhase.End
                    || action.phase == BehaviorEventPhase.Discrete,
                confirmed = true,
                newlyConfirmed = action.phase == BehaviorEventPhase.Begin
                    || action.phase == BehaviorEventPhase.Discrete,
                rejected = false,
                sendCancel = false,
                allowExpensiveUploads = true,
                protocolViolation = false,
                aoiScore = 1f,
                posterior = new[] { 0f, 1f },
                behaviorId = action.behaviorId,
                sequenceNumber = action.sequenceNumber,
                phase = action.phase,
                decisionLatencyMs = 0f,
                evidenceApplied = AoINovelEvidence.None,
                activeAoIBehaviorCount = 0,
                windowSampleCount = 0
            };
        }
        long decisionStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        bool protocolViolation = false;
        BehaviorFilterState state;
        bool newBehavior = action.phase == BehaviorEventPhase.Begin
            || action.phase == BehaviorEventPhase.Discrete;

        if (newBehavior)
        {
            if (activeBehaviors.ContainsKey(action.behaviorId))
            {
                activeBehaviors.Remove(action.behaviorId);
                protocolViolation = true;
            }
            state = CreateState(action);
            if (action.phase != BehaviorEventPhase.Discrete)
                activeBehaviors[action.behaviorId] = state;
        }
        else if (!activeBehaviors.TryGetValue(action.behaviorId, out state))
        {
            // Keep the stream usable, but expose the missing Begin in the experiment log.
            state = CreateState(action);
            state.startTimestamp = action.timestamp - Mathf.Max(0f, action.elapsedSeconds);
            activeBehaviors[action.behaviorId] = state;
            protocolViolation = true;
        }

        float interval = action.intervalSeconds > 0f
            ? action.intervalSeconds : Mathf.Max(0f, action.timestamp - state.lastTimestamp);
        float elapsed = action.elapsedSeconds > 0f
            ? action.elapsedSeconds : Mathf.Max(0f, action.timestamp - state.startTimestamp);
        action.intervalSeconds = interval;
        action.elapsedSeconds = elapsed;

        int sequence = action.sequenceNumber >= 0 ? action.sequenceNumber : state.nextSequence;
        if (sequence != state.nextSequence) protocolViolation = true;
        action.sequenceNumber = sequence;

        UpdateObservationWindow(state, action.timestamp);

        // Plain meaning: this behavior occurred while, or within 0.5 s after,
        // the user was operating a task-relevant object. This is local filter
        // evidence and does not add fields to the UBR sent to the edge.
        if (action.crossModalSupport == EvidenceValue.Unknown
            && action.intentType != IntentType.Interaction
            && HasRecentRelevantInteraction(action.timestamp))
            action.crossModalSupport = EvidenceValue.True;

        float[] prior = (float[])state.posterior.Clone();
        AoINovelEvidence novelEvidence = CollectNovelEvidence(state, action);
        if (action.intentType == IntentType.Interaction
            && state.targetRelevantObserved)
            lastRelevantInteractionTimestamp = action.timestamp;
        float[] result = ApplyEvidenceTokens(state.posterior, action.intentType,
            novelEvidence, config);
        float score = result[1];
        bool newlyConfirmed = !state.confirmed && score >= config.aoiThreshold;
        if (newlyConfirmed) state.confirmed = true;

        // A single threshold is used. Confirmation is absorbing for the remainder
        // of the complete behavior, so small posterior fluctuations cannot truncate
        // an already selected AoI behavior. Calibration mode still logs every row.
        bool ending = action.phase == BehaviorEventPhase.End
            || action.phase == BehaviorEventPhase.Discrete;
        bool wouldUpload = state.confirmed;
        bool mustClose = ending && state.edgeOpened;
        bool shouldUpload = CalibrationMode || wouldUpload || mustClose;
        state.rejected = ending && !state.confirmed;

        state.posterior = result;
        state.lastTimestamp = action.timestamp;
        state.nextSequence = sequence + 1;
        if (wouldUpload && !ending)
            state.edgeOpened = true;
        CurrentAoIScore = score;

        int activeAoICount = state.confirmed ? 1 : 0;
        foreach (KeyValuePair<string, BehaviorFilterState> pair in activeBehaviors)
            if (pair.Key != action.behaviorId && pair.Value.confirmed)
                activeAoICount++;

        float decisionLatencyMs = (float)(1000.0
            * (System.Diagnostics.Stopwatch.GetTimestamp() - decisionStarted)
            / System.Diagnostics.Stopwatch.Frequency);
        AoIDecision decision = new AoIDecision
        {
            shouldUpload = shouldUpload,
            wouldUpload = wouldUpload,
            crossedThreshold = newlyConfirmed,
            provisional = false,
            mustClose = mustClose,
            confirmed = state.confirmed,
            newlyConfirmed = newlyConfirmed,
            rejected = state.rejected,
            sendCancel = false,
            allowExpensiveUploads = state.confirmed,
            protocolViolation = protocolViolation,
            aoiScore = score,
            posterior = (float[])result.Clone(),
            behaviorId = action.behaviorId,
            sequenceNumber = sequence,
            phase = action.phase,
            decisionLatencyMs = decisionLatencyMs,
            evidenceApplied = novelEvidence,
            activeAoIBehaviorCount = activeAoICount,
            windowSampleCount = state.recentObservationTimes.Count
        };

        WriteEventRow(action, prior, decision);
        eventIndex++;
        if (EvidenceEvaluated != null) EvidenceEvaluated(action, decision);

        if (action.phase == BehaviorEventPhase.End
            || action.phase == BehaviorEventPhase.Discrete)
            activeBehaviors.Remove(action.behaviorId);

        return decision;
    }

    private BehaviorFilterState CreateState(ObservedAction action)
    {
        int intent = Mathf.Clamp((int)action.intentType, 0, IntentCount - 1);
        float meaningful = config.meaningfulPriorByIntent[intent];
        return new BehaviorFilterState
        {
            behaviorId = action.behaviorId,
            intentType = action.intentType,
            temporalType = action.temporalType,
            posterior = new[] { 1f - meaningful, meaningful },
            startTimestamp = action.timestamp,
            lastTimestamp = action.timestamp,
            nextSequence = 0
        };
    }

    private void UpdateObservationWindow(BehaviorFilterState state, float timestamp)
    {
        state.recentObservationTimes.Enqueue(timestamp);
        float earliest = timestamp - config.behaviorWindowSeconds;
        while (state.recentObservationTimes.Count > 0
            && state.recentObservationTimes.Peek() < earliest)
            state.recentObservationTimes.Dequeue();
    }

    private bool HasRecentRelevantInteraction(float timestamp)
    {
        float age = timestamp - lastRelevantInteractionTimestamp;
        return age >= 0f && age <= config.behaviorWindowSeconds;
    }

    private AoINovelEvidence CollectNovelEvidence(BehaviorFilterState state,
        ObservedAction action)
    {
        AoINovelEvidence novel = AoINovelEvidence.None;
        if (!state.targetEvidenceApplied
            && action.targetRelevant != EvidenceValue.Unknown)
        {
            state.targetEvidenceApplied = true;
            if (action.targetRelevant == EvidenceValue.True)
            {
                novel |= AoINovelEvidence.TargetRelevant;
                state.targetRelevantObserved = true;
            }
            else novel |= AoINovelEvidence.TargetIrrelevant;
        }
        if ((state.appliedEvidence & AoINovelEvidence.AttachConfirmed) == 0
            && action.attached == EvidenceValue.True)
            novel |= AoINovelEvidence.AttachConfirmed;
        if ((state.appliedEvidence & AoINovelEvidence.MotionStarted) == 0
            && action.motionObserved == EvidenceValue.True)
            novel |= AoINovelEvidence.MotionStarted;
        if ((state.appliedEvidence & AoINovelEvidence.Sustained250Ms) == 0
            && action.elapsedSeconds >= 0.25f)
            novel |= AoINovelEvidence.Sustained250Ms;
        if ((state.appliedEvidence & AoINovelEvidence.Sustained1000Ms) == 0
            && action.elapsedSeconds >= 1f)
            novel |= AoINovelEvidence.Sustained1000Ms;
        if ((state.appliedEvidence & AoINovelEvidence.Sustained3000Ms) == 0
            && action.elapsedSeconds >= 3f)
            novel |= AoINovelEvidence.Sustained3000Ms;
        if ((state.appliedEvidence & AoINovelEvidence.SameTaskBehaviorInWindow) == 0
            && action.crossModalSupport == EvidenceValue.True)
            novel |= AoINovelEvidence.SameTaskBehaviorInWindow;
        if ((state.appliedEvidence & AoINovelEvidence.NoTaskBehaviorInWindow) == 0
            && (state.appliedEvidence & AoINovelEvidence.SameTaskBehaviorInWindow) == 0
            && action.intentType == IntentType.Movement
            && (action.elapsedSeconds >= config.behaviorWindowSeconds
                || action.phase == BehaviorEventPhase.End))
            novel |= AoINovelEvidence.NoTaskBehaviorInWindow;
        if ((state.appliedEvidence & AoINovelEvidence.RepeatedInWindow) == 0
            && state.recentObservationTimes.Count >= 3)
            novel |= AoINovelEvidence.RepeatedInWindow;
        if ((state.appliedEvidence & AoINovelEvidence.SparseInWindow) == 0
            && (state.appliedEvidence & AoINovelEvidence.RepeatedInWindow) == 0
            && action.phase == BehaviorEventPhase.End
            && state.recentObservationTimes.Count <= 2)
            novel |= AoINovelEvidence.SparseInWindow;
        if ((state.appliedEvidence & AoINovelEvidence.EndedBefore250Ms) == 0
            && action.phase == BehaviorEventPhase.End
            && action.elapsedSeconds < 0.25f)
            novel |= AoINovelEvidence.EndedBefore250Ms;
        state.appliedEvidence |= novel;
        return novel;
    }

    public static float[] ApplyEvidenceTokens(float[] previous, IntentType intent,
        AoINovelEvidence evidence, AoIFilterConfig model)
    {
        ValidateAndNormalizeConfig(model);
        float meaningful = previous != null && previous.Length == 2
            ? Mathf.Clamp(previous[1], MinProbability, 1f - MinProbability) : 0.5f;
        double logNoise = Math.Log(1f - meaningful);
        double logAoI = Math.Log(meaningful);
        int category = Mathf.Clamp((int)intent, 0, IntentCount - 1);
        for (int token = 0; token < EvidenceTokenCount; token++)
        {
            AoINovelEvidence flag = (AoINovelEvidence)(1 << token);
            if ((evidence & flag) == 0) continue;
            int noiseIndex = ((category * StateCount) * EvidenceTokenCount) + token;
            int aoiIndex = ((category * StateCount + 1) * EvidenceTokenCount) + token;
            logNoise += Math.Log(Mathf.Max(MinProbability,
                model.evidenceTokenLikelihoodByIntentAndState[noiseIndex]));
            logAoI += Math.Log(Mathf.Max(MinProbability,
                model.evidenceTokenLikelihoodByIntentAndState[aoiIndex]));
        }
        double max = Math.Max(logNoise, logAoI);
        float noiseWeight = (float)Math.Exp(logNoise - max);
        float aoiWeight = (float)Math.Exp(logAoI - max);
        float total = Mathf.Max(MinProbability, noiseWeight + aoiWeight);
        return new[] { noiseWeight / total, aoiWeight / total };
    }

    public static float[] CalculatePosterior(float[] previous, ObservedAction action,
        AoIFilterConfig model, bool applyTransition, out float[] currentPrior)
    {
        float meaningful = previous != null && previous.Length == 2
            ? Mathf.Clamp(previous[1], MinProbability, 1f - MinProbability) : 0.5f;
        currentPrior = new[] { 1f - meaningful, meaningful };
        AoINovelEvidence evidence = AoINovelEvidence.None;
        if (action.targetRelevant == EvidenceValue.True)
            evidence |= AoINovelEvidence.TargetRelevant;
        else if (action.targetRelevant == EvidenceValue.False)
            evidence |= AoINovelEvidence.TargetIrrelevant;
        if (action.attached == EvidenceValue.True)
            evidence |= AoINovelEvidence.AttachConfirmed;
        if (action.motionObserved == EvidenceValue.True)
            evidence |= AoINovelEvidence.MotionStarted;
        if (action.elapsedSeconds >= 0.25f)
            evidence |= AoINovelEvidence.Sustained250Ms;
        if (action.elapsedSeconds >= 1f)
            evidence |= AoINovelEvidence.Sustained1000Ms;
        if (action.elapsedSeconds >= 3f)
            evidence |= AoINovelEvidence.Sustained3000Ms;
        if (action.crossModalSupport == EvidenceValue.True)
            evidence |= AoINovelEvidence.SameTaskBehaviorInWindow;
        if (action.phase == BehaviorEventPhase.End && action.elapsedSeconds < 0.25f)
            evidence |= AoINovelEvidence.EndedBefore250Ms;
        return ApplyEvidenceTokens(currentPrior, action.intentType, evidence, model);
    }

    public static int GetBucket(float value, float[] upperBounds)
    {
        if (upperBounds == null || upperBounds.Length != 3) return 0;
        if (value <= upperBounds[0]) return 0;
        if (value <= upperBounds[1]) return 1;
        if (value <= upperBounds[2]) return 2;
        return 3;
    }

    public static int GetIntervalBucket(float value, float[] upperBounds)
    {
        return GetBucket(value, upperBounds);
    }

    public static ReferentObservation GetReferentObservation(GameObject target)
    {
        if (target == null) return ReferentObservation.None;
        AoITaskRelevant marker = target.GetComponentInParent<AoITaskRelevant>();
        return marker != null && marker.IsRelevant
            ? ReferentObservation.Target : ReferentObservation.Object;
    }

    public static IntentType IntentForEvidence(ActionEvidenceType evidence)
    {
        switch (evidence)
        {
            case ActionEvidenceType.PoseMotion: return IntentType.Movement;
            case ActionEvidenceType.TargetGaze: return IntentType.EyeTracking;
            case ActionEvidenceType.VoiceActive: return IntentType.Communication;
            default: return IntentType.Interaction;
        }
    }

    public static EvidenceValue ResolveTargetRelevance(GameObject target)
    {
        if (target == null) return EvidenceValue.False;
        AoITaskRelevant marker = target.GetComponentInParent<AoITaskRelevant>();
        return marker == null ? EvidenceValue.Unknown
            : (marker.IsRelevant ? EvidenceValue.True : EvidenceValue.False);
    }

    public void ProcessObservedAction(ObservedAction action) { ObserveEvidence(action); }
    public AoIDecision ObserveBehaviorEvent(ObservedAction action) { return ObserveEvidence(action); }
    public void EndBehavior(string behaviorId) { activeBehaviors.Remove(behaviorId); }

    public void SetGroundTruthBehaviors(bool movement, bool observation,
        bool interaction, bool communication)
    {
        activeGroundTruth[0] = interaction;
        activeGroundTruth[1] = movement;
        activeGroundTruth[2] = observation;
        activeGroundTruth[3] = communication;
        groundTruthAvailable = true;
    }

    public void SetGroundTruthRequiresRelevantInteractionTarget(bool required)
    {
        requireRelevantTargetForInteractionGroundTruth = required;
    }

    public void SetGroundTruthEvidenceRequirements(bool movementNeedsContext,
        bool gazeNeedsRelevantTarget, bool gazeNeedsContext,
        bool communicationNeedsContext)
    {
        requireContextForMovementGroundTruth = movementNeedsContext;
        requireRelevantTargetForGazeGroundTruth = gazeNeedsRelevantTarget;
        requireContextForGazeGroundTruth = gazeNeedsContext;
        requireContextForCommunicationGroundTruth = communicationNeedsContext;
    }

    public void SetGroundTruthBehavior(IntentType behavior, bool active)
    {
        int index = (int)behavior;
        if (index >= 0 && index < IntentCount) activeGroundTruth[index] = active;
        groundTruthAvailable = true;
    }

    public void ClearGroundTruth()
    {
        Array.Clear(activeGroundTruth, 0, activeGroundTruth.Length);
        groundTruthAvailable = false;
    }

    public void SetExperimentContext(string participantId, string sessionId,
        string taskId, string trialId)
    {
        config.participantId = EmptyAsUnknown(participantId);
        config.sessionId = EmptyAsUnknown(sessionId);
        config.taskId = EmptyAsUnknown(taskId);
        config.trialId = EmptyAsUnknown(trialId);
        CloseWriterOnly();
        OpenExperimentLog();
    }

    private static string EmptyAsUnknown(string value)
    {
        return string.IsNullOrEmpty(value) ? "Unknown" : value.Trim();
    }

    public void SetAoICategories(bool interaction, bool movement,
        bool observation, bool communication)
    {
        relevantBehaviors[0] = config.aoiInteraction = interaction;
        relevantBehaviors[1] = config.aoiMovement = movement;
        relevantBehaviors[2] = config.aoiEyeTracking = observation;
        relevantBehaviors[3] = config.aoiCommunication = communication;
    }

    public void SetCalibrationMode(bool enabled)
    {
        CalibrationMode = config.calibrationMode = enabled;
    }

    /// <summary>
    /// Runtime ablation switch. When disabled, every behavior is uploaded without
    /// evaluating priors, likelihoods, evidence windows, or the AoI threshold.
    /// </summary>
    public void SetFilteringEnabled(bool enabled)
    {
        FilteringEnabled = enabled;
        ResetFilter();
    }

    public void EnableCalibrationMode() { SetCalibrationMode(true); }
    public void DisableCalibrationMode() { SetCalibrationMode(false); }
    public void LabelMovement() { SetGroundTruthBehavior(IntentType.Movement, true); }
    public void LabelObservation() { SetGroundTruthBehavior(IntentType.EyeTracking, true); }
    public void LabelInteraction() { SetGroundTruthBehavior(IntentType.Interaction, true); }
    public void LabelCommunication() { SetGroundTruthBehavior(IntentType.Communication, true); }
    public void BeginLabeledBehavior(IntentType intent) { SetGroundTruthBehavior(intent, true); }
    public void EndLabeledBehavior() { ClearGroundTruth(); }
    public void SetGroundTruth(IntentType intent, bool isAoI)
    {
        ClearGroundTruth();
        SetGroundTruthBehavior(intent, isAoI);
    }
    public void SetTaskAObjectSorting() { SetAoICategories(true, false, false, false); }
    public void SetTaskBCollaborativeAssembly() { SetAoICategories(true, false, false, true); }
    public void SetTaskCVisualInspection() { SetAoICategories(false, false, true, true); }

    public float GetIntentConfidence(IntentType intent)
    {
        float total = 0f;
        int count = 0;
        foreach (BehaviorFilterState state in activeBehaviors.Values)
        {
            if (state.intentType != intent) continue;
            total += state.posterior[1];
            count++;
        }
        if (count > 0) return total / count;
        int index = Mathf.Clamp((int)intent, 0, IntentCount - 1);
        return config.meaningfulPriorByIntent[index];
    }

    /// <summary>
    /// Returns the highest AoI posterior among currently confirmed behaviors.
    /// Expensive Context capture uses this value only after at least one behavior
    /// has crossed the AoI threshold. It is not a probability of Pose.
    /// </summary>
    public float GetConfirmedBehaviorAoIConfidence()
    {
        if (!FilteringEnabled) return 1f;

        float highest = 0f;
        foreach (BehaviorFilterState state in activeBehaviors.Values)
        {
            if (!state.confirmed || state.rejected) continue;
            highest = Mathf.Max(highest, state.posterior[1]);
        }
        return Mathf.Clamp01(highest);
    }

    /// <summary>
    /// Returns the highest AoI posterior among confirmed behaviors of one Intent.
    /// This overload is used by modality-specific capture such as hand sampling.
    /// </summary>
    public float GetConfirmedBehaviorAoIConfidence(IntentType intent)
    {
        if (!FilteringEnabled) return 1f;

        float highest = 0f;
        foreach (BehaviorFilterState state in activeBehaviors.Values)
        {
            if (state.intentType != intent || !state.confirmed || state.rejected) continue;
            highest = Mathf.Max(highest, state.posterior[1]);
        }
        return Mathf.Clamp01(highest);
    }

    public List<string> GetCurrentAoIBehaviorIds()
    {
        List<string> result = new List<string>();
        foreach (BehaviorFilterState state in activeBehaviors.Values)
            if (state.confirmed) result.Add(state.behaviorId);
        return result;
    }

    public List<ObservedAction> TakeBackfillActions() { return new List<ObservedAction>(); }
    public void ReportPayloadBytes(int rawPayloadBytes, int uploadedPayloadBytes) { }

    public void ResetFilter()
    {
        activeBehaviors.Clear();
        lastRelevantInteractionTimestamp = float.NegativeInfinity;
        CurrentAoIScore = 0f;
        eventIndex = 0;
    }

    private bool GroundTruthMeaningful(ObservedAction action)
    {
        int index = (int)action.intentType;
        bool meaningful = groundTruthAvailable && index >= 0 && index < IntentCount
            && activeGroundTruth[index];
        if (!meaningful) return false;
        if (requireRelevantTargetForInteractionGroundTruth
            && action.intentType == IntentType.Interaction)
            return action.targetRelevant == EvidenceValue.True;
        if (requireRelevantTargetForGazeGroundTruth
            && action.intentType == IntentType.EyeTracking
            && action.targetRelevant != EvidenceValue.True)
            return false;
        if (requireContextForMovementGroundTruth
            && action.intentType == IntentType.Movement
            && (action.crossModalSupport != EvidenceValue.True
                || action.elapsedSeconds < config.continuityThresholdSeconds))
            return false;
        if (requireContextForGazeGroundTruth
            && action.intentType == IntentType.EyeTracking
            && action.crossModalSupport != EvidenceValue.True)
            return false;
        if (requireContextForCommunicationGroundTruth
            && action.intentType == IntentType.Communication
            && action.crossModalSupport != EvidenceValue.True)
            return false;
        return true;
    }

    private void OpenExperimentLog()
    {
        if (config == null || !config.collectExperimentData) return;
        try
        {
            string directory = Path.Combine(Application.persistentDataPath, "AoIExperiments");
            Directory.CreateDirectory(directory);
            string name = SanitizeFileName(config.participantId + "_" + config.sessionId
                + "_" + config.taskId + "_" + config.trialId + "_"
                + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                + "_AoIContinuousEvents.csv");
            experimentWriter = new StreamWriter(Path.Combine(directory, name), false,
                new UTF8Encoding(false));
            experimentWriter.WriteLine(
                "participant_id,session_id,task_id,trial_id,device_mode,event_index," +
                "behavior_id,sequence_number,phase,temporal_type,intent_type,evidence_type," +
                "trigger_modality,current_behavior_state,timestamp,elapsed_seconds,interval_seconds,target_relevant,attached," +
                "motion_observed,cross_modal_support,translation_m,rotation_deg,linear_speed," +
                "angular_speed,target_name,prior_noise,prior_meaningful,posterior_noise," +
                "posterior_meaningful,tau_start,tau_keep,tau_stop,tau_low,tau_high,would_upload,actual_upload," +
                "provisional,confirmed,newly_confirmed,rejected,send_cancel,must_close,protocol_violation,ground_truth_available," +
                "ground_truth_meaningful,decision_latency_ms,payload_bytes,single_threshold," +
                "novel_evidence_mask,active_aoi_behavior_count,same_task_behavior_in_window," +
                "behavior_window_seconds,window_sample_count");
        }
        catch (Exception e)
        {
            Debug.LogWarning("AoI experiment log could not be opened: " + e.Message);
            experimentWriter = null;
        }
    }

    private void WriteEventRow(ObservedAction action, float[] prior, AoIDecision decision)
    {
        if (experimentWriter == null) return;
        StringBuilder row = new StringBuilder();
        AppendCsv(row, config.participantId); AppendCsv(row, config.sessionId);
        AppendCsv(row, config.taskId); AppendCsv(row, config.trialId);
        AppendCsv(row, DeviceMode); AppendCsv(row, eventIndex);
        AppendCsv(row, action.behaviorId); AppendCsv(row, action.sequenceNumber);
        AppendCsv(row, (int)action.phase); AppendCsv(row, (int)action.temporalType);
        AppendCsv(row, (int)action.intentType); AppendCsv(row, (int)action.evidenceType);
        AppendCsv(row, (int)action.modality); AppendCsv(row, (int)action.currentState);
        AppendCsv(row, action.timestamp); AppendCsv(row, action.elapsedSeconds);
        AppendCsv(row, action.intervalSeconds); AppendCsv(row, (int)action.targetRelevant);
        AppendCsv(row, (int)action.attached); AppendCsv(row, (int)action.motionObserved);
        AppendCsv(row, (int)action.crossModalSupport); AppendCsv(row, action.translationMeters);
        AppendCsv(row, action.rotationDegrees); AppendCsv(row, action.linearSpeed);
        AppendCsv(row, action.angularSpeed);
        AppendCsv(row, action.targetObject == null ? string.Empty : action.targetObject.name);
        AppendCsv(row, prior[0]); AppendCsv(row, prior[1]);
        AppendCsv(row, decision.posterior[0]); AppendCsv(row, decision.posterior[1]);
        AppendCsv(row, config.tauStart); AppendCsv(row, config.tauKeep); AppendCsv(row, config.tauStop);
        AppendCsv(row, config.tauLow); AppendCsv(row, config.tauHigh);
        AppendCsv(row, decision.wouldUpload ? 1 : 0);
        AppendCsv(row, decision.shouldUpload ? 1 : 0);
        AppendCsv(row, decision.provisional ? 1 : 0);
        AppendCsv(row, decision.confirmed ? 1 : 0);
        AppendCsv(row, decision.newlyConfirmed ? 1 : 0);
        AppendCsv(row, decision.rejected ? 1 : 0);
        AppendCsv(row, decision.sendCancel ? 1 : 0);
        AppendCsv(row, decision.mustClose ? 1 : 0);
        AppendCsv(row, decision.protocolViolation ? 1 : 0);
        AppendCsv(row, groundTruthAvailable ? 1 : 0);
        AppendCsv(row, GroundTruthMeaningful(action) ? 1 : 0);
        AppendCsv(row, decision.decisionLatencyMs);
        AppendCsv(row, action.payloadBytes);
        AppendCsv(row, config.aoiThreshold);
        AppendCsv(row, (int)decision.evidenceApplied);
        AppendCsv(row, decision.activeAoIBehaviorCount);
        AppendCsv(row, action.crossModalSupport == EvidenceValue.True ? 1 : 0);
        AppendCsv(row, config.behaviorWindowSeconds);
        AppendCsv(row, decision.windowSampleCount, false);
        experimentWriter.WriteLine(row.ToString());
        experimentWriter.Flush();
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value;
    }

    private static void AppendCsv(StringBuilder row, object value, bool comma = true)
    {
        if (value is float)
            row.Append(((float)value).ToString("R", CultureInfo.InvariantCulture));
        else
        {
            string text = value == null ? string.Empty : value.ToString();
            row.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
        }
        if (comma) row.Append(',');
    }

    private void OnApplicationQuit() { CloseWriterOnly(); }
    private void OnDestroy() { CloseWriterOnly(); }

    private void CloseWriterOnly()
    {
        if (experimentWriter == null) return;
        experimentWriter.Flush();
        experimentWriter.Dispose();
        experimentWriter = null;
    }
}
