using UnityEngine;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

// Handles AR device movement tracking via TransformTracker callbacks
public class ARMovementRecorder
{
    private readonly Camera userHeadCam;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private readonly float continueActionSamplingInterval;
    private readonly float minRGBDSamplingInterval;
    private readonly float maxRGBDSamplingInterval;

    private float lastMovementSamplingTime = 0f;
    private float lastRGBDSamplingTime = 0f;
    private float currentRGBDSamplingInterval = 1.0f;

    public ARMovementRecorder(Camera cam, Logger logger, AoIBayesianFilter filter,
        float continueActionSamplingInterval,
        float minRGBDSamplingInterval = 5.0f, float maxRGBDSamplingInterval = 10.0f)
    {
        userHeadCam = cam;
        this.logger = logger;
        aoiFilter = filter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
        this.minRGBDSamplingInterval = minRGBDSamplingInterval;
        this.maxRGBDSamplingInterval = maxRGBDSamplingInterval;
    }

    public int OnDeviceMovementBegin()
    {
        var observedAction = new ObservedAction(UserActionType.Pose, IntentType.Movement,
            "AR device navigation begin");
        int actionId = 0;
        if (aoiFilter.GetIntentConfidence(IntentType.Movement) >= aoiFilter.aoiThreshold)
        {
            actionId = logger.LogContinuousActionBegin("Navigate",
                "User walking around in physical space",
                ActionTriggerSource.ARHandHeld, userHeadCam.transform, null, null,
                ReferentType.None, ActionContextType.Physical);
        }
        aoiFilter.ProcessObservedAction(observedAction);
        lastMovementSamplingTime = Time.time;
        return actionId;
    }

    public int OnDeviceMovementContinue(int actionId)
    {
        if (actionId == 0) return 0;
        if (Time.time - lastMovementSamplingTime < continueActionSamplingInterval) return 0;
        var observedAction = new ObservedAction(UserActionType.Pose, IntentType.Movement,
            "AR device navigation continues");
        if (aoiFilter.GetIntentConfidence(IntentType.Movement) >= aoiFilter.aoiThreshold)
            logger.LogContinuousActionContinue(actionId);
        aoiFilter.ProcessObservedAction(observedAction);
        lastMovementSamplingTime = Time.time;
        return 0;
    }

    public int OnDeviceMovementEnd(int actionId)
    {
        logger.LogContinuousActionEnd(actionId);
        return 0;
    }

    // Adaptive RGBD sampling based on pose probability from AoI filter
    public void SampleRGBD()
    {
        float poseProbability = aoiFilter.CalculateMarginalProbability(UserActionType.Pose);
        currentRGBDSamplingInterval = Mathf.Lerp(maxRGBDSamplingInterval, minRGBDSamplingInterval, poseProbability);
        if (Time.time - lastRGBDSamplingTime >= currentRGBDSamplingInterval)
        {
            if (logger?.imageRecordManager != null)
            {
                logger.LogDiscreteAction("RGBDSample", "AR physical camera dynamic sampling",
                    ActionTriggerSource.ARHandHeld, userHeadCam.transform, null, null,
                    ReferentType.Physical, ActionContextType.Physical, LogRecordType.Context);
            }
            lastRGBDSamplingTime = Time.time;
        }
    }
}
