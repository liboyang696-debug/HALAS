using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public enum UserActionType
{
    Gesture,
    EyeTracking,
    Voice,
    Pose
}

public enum IntentType
{
    Interaction,
    Movement,
    EyeTracking,
    Communication
}

public class ObservedAction
{
    public UserActionType actionType;
    public IntentType intentType;
    public string actionDescription;
    public GameObject targetObject;
    public float timestamp;

    public ObservedAction(UserActionType action, IntentType intent, string description, GameObject target = null)
    {
        actionType = action;
        intentType = intent;
        actionDescription = description;
        targetObject = target;
        timestamp = Time.time;
    }
}

/// <summary>
/// Likelihood array order: [Gesture, EyeTracking, Voice, Pose].
/// </summary>
[Serializable]
public class AoIFilterConfig
{
    public float aoiThreshold = 0.6f;

    public float priorInteraction = 0.60f;
    public float priorMovement = 0.01f;
    public float priorEyeTracking = 0.01f;
    public float priorCommunication = 0.38f;

    public float[] likelihoodInteraction   = { 0.75f, 0.15f, 0.05f, 0.05f };
    public float[] likelihoodMovement      = { 0.25f, 0.25f, 0.25f, 0.25f };
    public float[] likelihoodEyeTracking   = { 0.20f, 0.50f, 0.25f, 0.05f };
    public float[] likelihoodCommunication = { 0.15f, 0.18f, 0.65f, 0.02f };
}

public class AoIBayesianFilter : MonoBehaviour
{
    public float aoiThreshold = 0.6f;

    private Dictionary<IntentType, float> posteriorProbabilities;
    private Dictionary<IntentType, Dictionary<UserActionType, float>> likelihoodMatrix;
    private List<ObservedAction> cachedActions;

    private float priorInteraction = 0.60f;
    private float priorMovement = 0.01f;
    private float priorEyeTracking = 0.01f;
    private float priorCommunication = 0.38f;

    private static readonly string ConfigFileName = "AoIFilterConfig.json";

    private static readonly UserActionType[] ActionTypeOrder =
    {
        UserActionType.Gesture,
        UserActionType.EyeTracking,
        UserActionType.Voice,
        UserActionType.Pose
    };

    [Serializable]
    private class CachedActionEntry
    {
        public string actionType;
        public string intentType;
        public string description;
        public string targetObject;
        public float timestamp;
    }

    [Serializable]
    private class AoICacheData
    {
        public string exportTime;
        public int totalCount;
        public List<CachedActionEntry> actions;
    }

    private void Awake()
    {
        AoIFilterConfig config = LoadConfig();
        ApplyConfig(config);
        InitializeProbabilities();
        InitializeLikelihoodMatrix(config);
        cachedActions = new List<ObservedAction>();
    }

    private AoIFilterConfig LoadConfig()
    {
        string path = Path.Combine(Application.streamingAssetsPath, ConfigFileName);
        if (!File.Exists(path))
        {
            Debug.Log("AoI Config file not found, using default values.");
            return new AoIFilterConfig();
        }

        try
        {
            string json = File.ReadAllText(path);
            var config = JsonUtility.FromJson<AoIFilterConfig>(json);
            Debug.Log($"AoI Config loaded from {path}");
            return config;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"AoI Failed to load config: {e.Message}, using defaults.");
            return new AoIFilterConfig();
        }
    }

    private void ApplyConfig(AoIFilterConfig config)
    {
        aoiThreshold = config.aoiThreshold;
        priorInteraction = config.priorInteraction;
        priorMovement = config.priorMovement;
        priorEyeTracking = config.priorEyeTracking;
        priorCommunication = config.priorCommunication;
    }

    private Dictionary<UserActionType, float> BuildLikelihoodRow(float[] values, float[] defaults)
    {
        float[] src = (values != null && values.Length == ActionTypeOrder.Length) ? values : defaults;
        var row = new Dictionary<UserActionType, float>();
        for (int i = 0; i < ActionTypeOrder.Length; i++)
            row[ActionTypeOrder[i]] = src[i];
        return row;
    }

    private void InitializeProbabilities()
    {
        posteriorProbabilities = new Dictionary<IntentType, float>
        {
            { IntentType.Interaction, priorInteraction },
            { IntentType.Movement, priorMovement },
            { IntentType.EyeTracking, priorEyeTracking },
            { IntentType.Communication, priorCommunication }
        };
        NormalizeProbabilities();
    }

    // Likelihood matrix: P(Action | Intent)
    private void InitializeLikelihoodMatrix(AoIFilterConfig config)
    {
        float[] defInteraction   = { 0.75f, 0.15f, 0.05f, 0.05f };
        float[] defMovement      = { 0.25f, 0.25f, 0.25f, 0.25f };
        float[] defEyeTracking   = { 0.20f, 0.50f, 0.25f, 0.05f };
        float[] defCommunication = { 0.15f, 0.18f, 0.65f, 0.02f };

        likelihoodMatrix = new Dictionary<IntentType, Dictionary<UserActionType, float>>
        {
            [IntentType.Interaction]   = BuildLikelihoodRow(config.likelihoodInteraction,   defInteraction),
            [IntentType.Movement]      = BuildLikelihoodRow(config.likelihoodMovement,      defMovement),
            [IntentType.EyeTracking]   = BuildLikelihoodRow(config.likelihoodEyeTracking,   defEyeTracking),
            [IntentType.Communication] = BuildLikelihoodRow(config.likelihoodCommunication,  defCommunication)
        };
    }

    private void NormalizeProbabilities()
    {
        float sum = 0f;
        foreach (var prob in posteriorProbabilities.Values)
            sum += prob;

        if (sum <= 0) return;
        var keys = new List<IntentType>(posteriorProbabilities.Keys);
        foreach (var key in keys)
            posteriorProbabilities[key] /= sum;
    }

    // Marginal probability: P(Action) = Σ P(Action|Intent_i) × P(Intent_i)
    public float CalculateMarginalProbability(UserActionType actionType)
    {
        float marginal = 0f;
        foreach (var intent in posteriorProbabilities.Keys)
            marginal += likelihoodMatrix[intent][actionType] * posteriorProbabilities[intent];
        return marginal;
    }

    // Process an observed action: filter by AoI threshold, then update posterior
    public void ProcessObservedAction(ObservedAction action)
    {
        if (action == null) return;
        float intentConfidence = posteriorProbabilities[action.intentType];

        if (intentConfidence >= aoiThreshold)
        {
            Debug.Log($"<color=green>[High AoI] {action.actionDescription}</color>");
        }
        else
        {
            cachedActions.Add(action);
            Debug.Log($"<color=yellow>[Low AoI -> Cached] {action.actionDescription}</color>");
        }

        UpdatePosterior(action);
    }

    // Standard Bayesian update: P(Intent|Action) = P(Action|Intent) * P(Intent) / P(Action)
    private void UpdatePosterior(ObservedAction action)
    {
        float marginal = CalculateMarginalProbability(action.actionType);
        if (marginal == 0f) marginal = 0.0001f;

        var newPosterior = new Dictionary<IntentType, float>();
        foreach (var intent in posteriorProbabilities.Keys)
        {
            float likelihood = likelihoodMatrix[intent][action.actionType];
            float prior = posteriorProbabilities[intent];
            newPosterior[intent] = (likelihood * prior) / marginal;
        }

        posteriorProbabilities = newPosterior;
        NormalizeProbabilities();
    }

    public float GetIntentConfidence(IntentType intent)
    {
        return posteriorProbabilities.ContainsKey(intent) ? posteriorProbabilities[intent] : 0f;
    }

    public void ResetFilter()
    {
        InitializeProbabilities();
        cachedActions.Clear();
    }

    private void OnApplicationQuit()
    {
        ExportCachedActionsToJson();
    }

    private void ExportCachedActionsToJson()
    {
        if (cachedActions == null || cachedActions.Count == 0) return;

        var cacheData = new AoICacheData
        {
            exportTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            totalCount = cachedActions.Count,
            actions = new List<CachedActionEntry>()
        };

        foreach (var action in cachedActions)
        {
            cacheData.actions.Add(new CachedActionEntry
            {
                actionType = action.actionType.ToString(),
                intentType = action.intentType.ToString(),
                description = action.actionDescription,
                targetObject = action.targetObject != null ? action.targetObject.name : null,
                timestamp = action.timestamp
            });
        }

        string json = JsonUtility.ToJson(cacheData, true);
        string filePath = Path.Combine(Application.persistentDataPath, "AoICache.json");
        File.WriteAllText(filePath, json);

        Debug.Log($"[AoI Cache] Exported {cachedActions.Count} cached actions to {filePath}");
    }
}
