using System;
using UnityEngine;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VRMovementRecorder
{
    private readonly Camera userHeadCam;
    private readonly Transform vrHMDTransform;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private readonly float continueActionSamplingInterval;
    private readonly float bayesianUpdateInterval;
    private readonly float minRGBDSamplingInterval;
    private readonly float maxRGBDSamplingInterval;
    private readonly float moveSpeed;

    private int leftMoveActionID;
    private int rightMoveActionID;
    private float lastMovementSamplingTime = 0f;
    private float lastMovementUploadTime = 0f;
    private float lastRGBDSamplingTime = 0f;
    private float currentRGBDSamplingInterval = 1.0f;
    private string movementBehaviorId = string.Empty;
    private string movementActionName = "Navigate";
    private float movementStartTime;
    private Vector3 lastMovementPosition;
    private Quaternion lastMovementRotation = Quaternion.identity;

    public VRMovementRecorder(Camera userHeadCam, Transform vrHMDTransform, Logger logger,
        AoIBayesianFilter aoiFilter, float continueActionSamplingInterval,
        float bayesianUpdateInterval,
        float minRGBDSamplingInterval, float maxRGBDSamplingInterval, float moveSpeed)
    {
        this.userHeadCam = userHeadCam;
        this.vrHMDTransform = vrHMDTransform;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
        this.bayesianUpdateInterval = Mathf.Clamp(bayesianUpdateInterval, 0.01f, 0.05f);
        this.minRGBDSamplingInterval = minRGBDSamplingInterval;
        this.maxRGBDSamplingInterval = maxRGBDSamplingInterval;
        this.moveSpeed = moveSpeed;
        // Allow the first eligible Context frame immediately instead of waiting
        // one full sampling interval after Play starts.
        lastRGBDSamplingTime = Time.time - Mathf.Max(0f, minRGBDSamplingInterval);
    }


    public int OnHMDMovementBegin()
    {
        return OnHMDMovementBegin(null);
    }

    public int OnHMDMovementBegin(string actionName)
    {
        movementActionName = string.IsNullOrWhiteSpace(actionName)
            ? "Navigate" : actionName.Trim();
        movementBehaviorId = "vr-hmd-movement-" + Guid.NewGuid().ToString("N");
        movementStartTime = Time.unscaledTime;
        lastMovementPosition = vrHMDTransform.position;
        lastMovementRotation = vrHMDTransform.rotation;
        var observedAction = new ObservedAction(ActionEvidenceType.PoseMotion,
            "User's VRHMD movement", null, 0, movementBehaviorId,
            BehaviorEventPhase.Begin, TriggerModality.HMD,
            CurrentBehaviorState.Navigating, IntentType.Movement,
            BehaviorTemporalType.Continuous);
        observedAction.sequenceNumber = 0;
        observedAction.motionObserved = EvidenceValue.True;
        AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
        int actionId = decision.shouldUpload ? StartMovementUpload() : 0;
        AoIUBRMetadataBridge.Register(actionId, observedAction, decision);
        lastMovementSamplingTime = Time.unscaledTime;
        lastMovementUploadTime = Time.unscaledTime;
        return actionId;
    }

    public int OnHMDMovementContinue(int actionId)
    {
        if (Time.unscaledTime - lastMovementSamplingTime < bayesianUpdateInterval) return actionId;
        float now = Time.unscaledTime;
        float interval = Mathf.Max(0.0001f, now - lastMovementSamplingTime);
        float translation = Vector3.Distance(lastMovementPosition, vrHMDTransform.position);
        float rotation = Quaternion.Angle(lastMovementRotation, vrHMDTransform.rotation);
        var observedAction = new ObservedAction(ActionEvidenceType.PoseMotion,
            "User's VRHMD movement continues", null, 0, movementBehaviorId,
            BehaviorEventPhase.Continue, TriggerModality.HMD,
            CurrentBehaviorState.Navigating, IntentType.Movement,
            BehaviorTemporalType.Continuous);
        observedAction.elapsedSeconds = Mathf.Max(0f, now - movementStartTime);
        observedAction.intervalSeconds = interval;
        observedAction.translationMeters = translation;
        observedAction.rotationDegrees = rotation;
        observedAction.linearSpeed = translation / interval;
        observedAction.angularSpeed = rotation / interval;
        observedAction.motionObserved = EvidenceValue.True;
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
                lastMovementUploadTime = now;
            }
            else if (now - lastMovementUploadTime >= continueActionSamplingInterval)
            {
                logger.LogContinuousActionContinue(actionId);
                AoIUBRMetadataBridge.Register(actionId, observedAction, decision);
                lastMovementUploadTime = now;
            }
        }
        lastMovementSamplingTime = now;
        lastMovementPosition = vrHMDTransform.position;
        lastMovementRotation = vrHMDTransform.rotation;
        return actionId;
    }

    public int OnHMDMovementEnd(int actionId)
    {
        var end = new ObservedAction(ActionEvidenceType.PoseMotion,
            "User's VRHMD movement ends", null, 0, movementBehaviorId,
            BehaviorEventPhase.End, TriggerModality.HMD,
            CurrentBehaviorState.Navigating, IntentType.Movement,
            BehaviorTemporalType.Continuous);
        end.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - movementStartTime);
        end.motionObserved = EvidenceValue.False;
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
        movementActionName = "Navigate";
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
        return logger.LogContinuousActionBegin(
            movementActionName, "User is moving in VR space",
            ActionTriggerSource.XRHMD, vrHMDTransform, null, null,
            ReferentType.None, ActionContextType.Virtual, LogRecordType.UBRLog);
    }


    public int OnLeftJoystickStart(ReferentMetadata referentMetadata)
    {
        ApplyJoystickMovement(referentMetadata.joystickInput);
        return 0;
    }

    public int OnLeftJoystickHeld((ReferentMetadata referentMetadata, bool record) args)
    {
        ApplyJoystickMovement(args.referentMetadata.joystickInput);
        return 0;
    }

    public int OnLeftJoystickEnd(ReferentMetadata referentMetadata)
    {
        if (leftMoveActionID > 0) logger.LogContinuousActionEnd(leftMoveActionID);
        return 0;
    }

    public int OnRightJoystickStart(ReferentMetadata referentMetadata)
    {
        ApplyJoystickMovement(referentMetadata.joystickInput);
        return 0;
    }

    public int OnRightJoystickHeld((ReferentMetadata referentMetadata, bool record) args)
    {
        ApplyJoystickMovement(args.referentMetadata.joystickInput);
        return 0;
    }

    public int OnRightJoystickEnd(ReferentMetadata referentMetadata)
    {
        if (rightMoveActionID > 0) logger.LogContinuousActionEnd(rightMoveActionID);
        return 0;
    }

    private void ApplyJoystickMovement(Vector2 joystickInput)
    {
        Vector3 movement = new Vector3(
            joystickInput.x * moveSpeed * Time.deltaTime, 0,
            joystickInput.y * moveSpeed * Time.deltaTime);
        userHeadCam.transform.parent.position += movement;
    }


    // Dynamic Context sampling based on the AoI posterior of confirmed behaviors.
    public void SampleRGBD()
    {
        // With AoI enabled, expensive RGB-D capture is allowed only while at
        // least one behavior is confirmed.  The No-AoI ablation deliberately
        // bypasses that gate, so it must continue sampling the full context
        // stream instead of silently producing zero Context messages.
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
                logger.LogDiscreteAction("RGBDSample", "User uses RGBD camera dynamic sampling",
                    ActionTriggerSource.XRHMD, userHeadCam.transform, null, null,
                    ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.Context);
            }
            lastRGBDSamplingTime = Time.time;
        }
    }
}
