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
    private readonly float bayesianUpdateInterval;
    private readonly float minRGBDSamplingInterval;
    private readonly float maxRGBDSamplingInterval;

    private float lastMovementSamplingTime = 0f;
    private float lastMovementUploadTime = 0f;
    private float lastRGBDSamplingTime = 0f;
    private float currentRGBDSamplingInterval = 1.0f;
    private string movementBehaviorId = string.Empty;
    private float movementStartTime;

    public ARMovementRecorder(Camera cam, Logger logger, AoIBayesianFilter filter,
        float continueActionSamplingInterval,
        float bayesianUpdateInterval,
        float minRGBDSamplingInterval = 5.0f, float maxRGBDSamplingInterval = 10.0f)
    {
        userHeadCam = cam;
        this.logger = logger;
        aoiFilter = filter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
        this.bayesianUpdateInterval = Mathf.Clamp(bayesianUpdateInterval, 0.01f, 0.05f);
        this.minRGBDSamplingInterval = minRGBDSamplingInterval;
        this.maxRGBDSamplingInterval = maxRGBDSamplingInterval;
    }

    public int OnDeviceMovementBegin()
    {
        movementBehaviorId = "ar-device-movement-" + System.Guid.NewGuid().ToString("N");
        movementStartTime = Time.unscaledTime;
        var observedAction = new ObservedAction(ActionEvidenceType.PoseMotion,
            "AR device navigation begin", null, 0, movementBehaviorId,
            BehaviorEventPhase.Begin, TriggerModality.Handheld,
            CurrentBehaviorState.Navigating);
        AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
        int actionId = decision.shouldUpload ? StartMovementUpload() : 0;
        AoIUBRMetadataBridge.Register(actionId, observedAction, decision);
        lastMovementSamplingTime = Time.unscaledTime;
        lastMovementUploadTime = Time.unscaledTime;
        return actionId;
    }

    public int OnDeviceMovementContinue(int actionId)
    {
        if (Time.unscaledTime - lastMovementSamplingTime < bayesianUpdateInterval) return actionId;
        var observedAction = new ObservedAction(ActionEvidenceType.PoseMotion,
            "AR device navigation continues", null, 0, movementBehaviorId,
            BehaviorEventPhase.Continue, TriggerModality.Handheld,
            CurrentBehaviorState.Navigating);
        observedAction.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - movementStartTime);
        observedAction.intervalSeconds = Mathf.Max(0.0001f,
            Time.unscaledTime - lastMovementSamplingTime);
        AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
        if (decision.sendCancel)
        {
            CancelUpload(actionId);
            actionId = 0;
        }
        else if (decision.confirmed && !decision.rejected)
        {
            if (actionId <= 0)
            {
                actionId = StartMovementUpload();
                AoIUBRMetadataBridge.Register(actionId, observedAction, decision,
                    BehaviorEventPhase.Begin);
                lastMovementUploadTime = Time.unscaledTime;
            }
            else if (Time.unscaledTime - lastMovementUploadTime
                >= continueActionSamplingInterval)
            {
                logger.LogContinuousActionContinue(actionId);
                AoIUBRMetadataBridge.Register(actionId, observedAction, decision);
                lastMovementUploadTime = Time.unscaledTime;
            }
        }
        lastMovementSamplingTime = Time.unscaledTime;
        return actionId;
    }

    public int OnDeviceMovementEnd(int actionId)
    {
        var end = new ObservedAction(ActionEvidenceType.PoseMotion,
            "AR device navigation ends", null, 0, movementBehaviorId,
            BehaviorEventPhase.End, TriggerModality.Handheld,
            CurrentBehaviorState.Navigating);
        end.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - movementStartTime);
        AoIDecision decision = aoiFilter.ObserveEvidence(end);
        if (decision.sendCancel && actionId > 0)
            CancelUpload(actionId);
        else if (decision.shouldUpload)
        {
            if (actionId <= 0)
            {
                actionId = StartMovementUpload();
                AoIUBRMetadataBridge.Register(actionId, end, decision,
                    BehaviorEventPhase.Begin);
            }
            logger.LogContinuousActionEnd(actionId);
            AoIUBRMetadataBridge.Register(actionId, end, decision);
        }
        movementBehaviorId = string.Empty;
        return 0;
    }

    private void CancelUpload(int actionId)
    {
        if (actionId <= 0) return;
        logger.DiscardContinuousAction(actionId);
        if (UBRParserAndSender.Instance != null)
            UBRParserAndSender.Instance.CancelAoIAction(actionId);
    }

    private int StartMovementUpload()
    {
        return logger.LogContinuousActionBegin("Navigate",
            "User walking around in physical space",
            ActionTriggerSource.ARHandHeld, userHeadCam.transform, null, null,
            ReferentType.None, ActionContextType.Physical);
    }

    // Dynamic Context sampling based on the AoI posterior of confirmed behaviors.
    public void SampleRGBD()
    {
        if (aoiFilter.FilteringEnabled && !aoiFilter.HasConfirmedBehavior) return;
        float behaviorAoIConfidence = aoiFilter.FilteringEnabled
            ? aoiFilter.GetConfirmedBehaviorAoIConfidence()
            : 1f;
        currentRGBDSamplingInterval = Mathf.Lerp(maxRGBDSamplingInterval,
            minRGBDSamplingInterval, behaviorAoIConfidence);
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
