using System;
using UnityEngine;
using Unity.XR.PXR;
using UnityEngine.XR.Interaction.Toolkit;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VREyeTrackingRecorder
{
    private readonly Camera userHeadCam;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private readonly float continueActionSamplingInterval;
    private readonly float bayesianUpdateInterval;

    private bool trackingSupported = false;
    private bool isTracking = false;
    private TrackingStateCode trackingState;
    private EyeTrackingData eyeTrackingData;
    private int supportedModesCount = 0;
    private EyeTrackingMode[] supportedModes = null;

    private const float rayLength = 100f;
    private const float gazeDwellThreshold = 0.5f;
    private GameObject currentGazedObject = null;
    private float gazeStartTime = 0f;
    private bool hasLoggedGaze = false;
    private int gazeActionID = 0;
    private string gazeActionName = "EyeTracking";
    private string gazeBehaviorId = string.Empty;
    private float lastGazeSamplingTime = 0f;
    private float lastGazeUploadTime = 0f;
    private float emittedGazeStartTime = 0f;

    public VREyeTrackingRecorder(Camera userHeadCam, Logger logger, AoIBayesianFilter aoiFilter,
        float continueActionSamplingInterval, float bayesianUpdateInterval)
    {
        this.userHeadCam = userHeadCam;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
        this.bayesianUpdateInterval = Mathf.Clamp(bayesianUpdateInterval, 0.01f, 0.05f);
    }

    // Check hardware support and start PICO eye tracking
    public void Initialize()
    {
        trackingState = (TrackingStateCode)PXR_MotionTracking.GetEyeTrackingSupported(
            ref trackingSupported, ref supportedModesCount, ref supportedModes);
        if (trackingState == TrackingStateCode.PXR_MT_SUCCESS && trackingSupported)
        {
            EyeTrackingStartInfo info = new EyeTrackingStartInfo();
            info.needCalibration = 1;
            info.mode = EyeTrackingMode.PXR_ETM_BOTH;
            trackingState = (TrackingStateCode)PXR_MotionTracking.StartEyeTracking(ref info);
        }
    }

    // Read hardware eye tracking data and perform gaze raycast
    public void Tick()
    {
        if (!trackingSupported) return;

        EyeTrackingState eyeTrackingState = new EyeTrackingState();
        trackingState = (TrackingStateCode)PXR_MotionTracking.GetEyeTrackingState(
            ref isTracking, ref eyeTrackingState);
        if (!isTracking) return;

        EyeTrackingDataGetInfo getInfo = new EyeTrackingDataGetInfo();
        getInfo.displayTime = 0;
        getInfo.flags = EyeTrackingDataGetFlags.PXR_EYE_DEFAULT
                      | EyeTrackingDataGetFlags.PXR_EYE_POSITION
                      | EyeTrackingDataGetFlags.PXR_EYE_ORIENTATION;
        trackingState = (TrackingStateCode)PXR_MotionTracking.GetEyeTrackingData(
            ref getInfo, ref eyeTrackingData);

        // Compute gaze ray in world space from combined eye data (index 2)
        PxrPose eyeData = eyeTrackingData.eyeDatas[2].pose;
        var origin = eyeData.position.ToVector3();
        var direction = eyeData.orientation.ToQuat();
        Quaternion correctedDirection = new Quaternion(-direction.x, -direction.y, direction.z, direction.w);
        Vector3 worldOrigin = userHeadCam.transform.TransformPoint(origin) - new Vector3(0, 1.16144f, 0);
        Vector3 worldDirection = correctedDirection * Vector3.forward;

        RaycastHit hit;
        GameObject gazeTarget = null;
        if (Physics.Raycast(worldOrigin, worldDirection, out hit, rayLength))
            gazeTarget = ResolveGazeTarget(hit.collider);
        if (gazeTarget != null)
        {
            HandleGazeHit(gazeTarget);
        }
        else
        {
            HandleGazeLost();
        }
    }

    private static GameObject ResolveGazeTarget(Collider hitCollider)
    {
        if (hitCollider == null) return null;

        AoITaskRelevant marker = hitCollider.GetComponentInParent<AoITaskRelevant>();
        if (marker != null) return marker.gameObject;

        XRGrabInteractable grabbable = hitCollider.GetComponentInParent<XRGrabInteractable>();
        if (grabbable != null) return grabbable.gameObject;

        Transform current = hitCollider.transform;
        while (current != null)
        {
            if (current.CompareTag("Target")) return current.gameObject;
            current = current.parent;
        }
        return null;
    }


    // Begin logging gaze at a target object. Returns actionID (0 if filtered by AoI).
    public int LogGazeBegin(GameObject target)
    {
        return LogGazeBegin(target, null);
    }

    public int LogGazeBegin(GameObject target, string actionName)
    {
        currentGazedObject = target;
        gazeActionName = string.IsNullOrWhiteSpace(actionName)
            ? "EyeTracking" : actionName.Trim();
        gazeBehaviorId = $"vr-gaze-{target.GetInstanceID()}-{Guid.NewGuid():N}";
        emittedGazeStartTime = Time.unscaledTime;
        var observedAction = new ObservedAction(ActionEvidenceType.TargetGaze,
            $"User gazing at {target.name}", target, 0, gazeBehaviorId,
            BehaviorEventPhase.Begin, TriggerModality.Eye,
            CurrentBehaviorState.TargetFocused, IntentType.EyeTracking,
            BehaviorTemporalType.Continuous);
        observedAction.sequenceNumber = 0;
        observedAction.targetRelevant = AoIBayesianFilter.ResolveTargetRelevance(target);
        AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
        if (!decision.shouldUpload) return 0;
        int actionId = StartGazeUpload(target, captureReferent: !decision.provisional);
        AoIUBRMetadataBridge.Register(actionId, observedAction, decision);
        lastGazeUploadTime = Time.unscaledTime;
        return actionId;
    }

    // Continue logging an ongoing gaze action
    public int LogGazeContinue(int actionId, GameObject target)
    {
        var observedAction = new ObservedAction(ActionEvidenceType.TargetGaze,
            $"User gazing at {target.name}", target, 0, gazeBehaviorId,
            BehaviorEventPhase.Continue, TriggerModality.Eye,
            CurrentBehaviorState.TargetFocused, IntentType.EyeTracking,
            BehaviorTemporalType.Continuous);
        observedAction.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - emittedGazeStartTime);
        observedAction.intervalSeconds = continueActionSamplingInterval;
        observedAction.targetRelevant = AoIBayesianFilter.ResolveTargetRelevance(target);
        AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
        if (decision.sendCancel)
        {
            CancelUpload(actionId);
            return 0;
        }
        if (!decision.confirmed || decision.rejected) return actionId;
        if (decision.newlyConfirmed && actionId > 0)
            logger.EnableContinuousActionReferent(actionId);
        if (actionId <= 0)
        {
            actionId = StartGazeUpload(target);
            AoIUBRMetadataBridge.Register(actionId, observedAction, decision,
                BehaviorEventPhase.Begin);
            lastGazeUploadTime = Time.unscaledTime;
        }
        else if (Time.unscaledTime - lastGazeUploadTime >= continueActionSamplingInterval)
        {
            logger.LogContinuousActionContinue(actionId);
            AoIUBRMetadataBridge.Register(actionId, observedAction, decision);
            lastGazeUploadTime = Time.unscaledTime;
        }
        return actionId;
    }

    // End a gaze action
    public void LogGazeEnd(int actionId)
    {
        if (!string.IsNullOrEmpty(gazeBehaviorId))
        {
            var end = new ObservedAction(ActionEvidenceType.TargetGaze,
                "User gaze ends", currentGazedObject, 0, gazeBehaviorId,
                BehaviorEventPhase.End, TriggerModality.Eye,
                CurrentBehaviorState.TargetFocused, IntentType.EyeTracking,
                BehaviorTemporalType.Continuous);
            end.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - emittedGazeStartTime);
            end.targetRelevant = AoIBayesianFilter.ResolveTargetRelevance(currentGazedObject);
            AoIDecision decision = aoiFilter.ObserveEvidence(end);
            if (decision.sendCancel && actionId > 0)
                CancelUpload(actionId);
            else if (decision.shouldUpload)
            {
                if (actionId <= 0 && currentGazedObject != null)
                {
                    actionId = StartGazeUpload(currentGazedObject);
                    AoIUBRMetadataBridge.Register(actionId, end, decision,
                        BehaviorEventPhase.Begin);
                }
                logger.LogContinuousActionEnd(actionId);
                AoIUBRMetadataBridge.Register(actionId, end, decision);
            }
        }
        gazeBehaviorId = string.Empty;
        gazeActionName = "EyeTracking";
    }

    private void CancelUpload(int actionId)
    {
        if (actionId <= 0) return;
        logger.DiscardContinuousAction(actionId);
        if (UBRParserAndSender.Instance != null)
            UBRParserAndSender.Instance.CancelAoIAction(actionId);
    }

    private int StartGazeUpload(GameObject target, bool captureReferent = true)
    {
        return logger.LogContinuousActionBegin(gazeActionName, "Visual Attention",
            ActionTriggerSource.XRHMD, userHeadCam.transform, target, target.transform,
            ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.UBRLog,
            captureReferent);
    }


    private void HandleGazeHit(GameObject hitObject)
    {
        if (currentGazedObject != hitObject)
        {
            if (currentGazedObject != null && hasLoggedGaze)
                LogGazeEnd(gazeActionID);
            currentGazedObject = hitObject;
            gazeStartTime = Time.time;
            hasLoggedGaze = false;
            gazeActionID = 0;
            lastGazeSamplingTime = Time.time;
        }
        else
        {
            float gazeDuration = Time.time - gazeStartTime;
            if (gazeDuration >= gazeDwellThreshold && !hasLoggedGaze)
            {
                hasLoggedGaze = true;
                gazeActionID = LogGazeBegin(hitObject);
                lastGazeSamplingTime = Time.time;
            }
            else if (hasLoggedGaze && (Time.time - lastGazeSamplingTime) >= bayesianUpdateInterval)
            {
                gazeActionID = LogGazeContinue(gazeActionID, hitObject);
                lastGazeSamplingTime = Time.time;
            }
        }
    }

    private void HandleGazeLost()
    {
        if (currentGazedObject != null)
        {
            if (hasLoggedGaze)
                LogGazeEnd(gazeActionID);
            currentGazedObject = null;
            gazeStartTime = 0f;
            hasLoggedGaze = false;
            gazeActionID = 0;
        }
    }
}
