using UnityEngine;
using Unity.XR.PXR;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VREyeTrackingRecorder
{
    private readonly Camera userHeadCam;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private readonly float continueActionSamplingInterval;

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
    private float lastGazeSamplingTime = 0f;

    public VREyeTrackingRecorder(Camera userHeadCam, Logger logger, AoIBayesianFilter aoiFilter,
        float continueActionSamplingInterval)
    {
        this.userHeadCam = userHeadCam;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
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
        if (Physics.Raycast(worldOrigin, worldDirection, out hit, rayLength)
            && hit.collider.gameObject.CompareTag("Target"))
        {
            HandleGazeHit(hit.collider.gameObject);
        }
        else
        {
            HandleGazeLost();
        }
    }


    // Begin logging gaze at a target object. Returns actionID (0 if filtered by AoI).
    public int LogGazeBegin(GameObject target)
    {
        var observedAction = new ObservedAction(UserActionType.EyeTracking, IntentType.EyeTracking,
            $"User gazing at {target.name}", null);
        int actionId = 0;
        if (aoiFilter.GetIntentConfidence(IntentType.EyeTracking) >= aoiFilter.aoiThreshold)
        {
            actionId = logger.LogContinuousActionBegin("EyeTracking", "Visual Attention",
                ActionTriggerSource.XRHMD, userHeadCam.transform, target, target.transform,
                ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.UBRLog);
        }
        aoiFilter.ProcessObservedAction(observedAction);
        return actionId;
    }

    // Continue logging an ongoing gaze action
    public void LogGazeContinue(int actionId, GameObject target)
    {
        if (actionId == 0) return;
        var observedAction = new ObservedAction(UserActionType.EyeTracking, IntentType.EyeTracking,
            $"User gazing at {target.name}", target);
        if (aoiFilter.GetIntentConfidence(IntentType.EyeTracking) >= aoiFilter.aoiThreshold)
            logger.LogContinuousActionContinue(actionId);
        aoiFilter.ProcessObservedAction(observedAction);
    }

    // End a gaze action
    public void LogGazeEnd(int actionId)
    {
        if (actionId == 0) return;
        logger.LogContinuousActionEnd(actionId);
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
            else if (hasLoggedGaze && (Time.time - lastGazeSamplingTime) >= continueActionSamplingInterval)
            {
                LogGazeContinue(gazeActionID, hitObject);
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
