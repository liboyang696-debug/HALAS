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
    private readonly float minRGBDSamplingInterval;
    private readonly float maxRGBDSamplingInterval;
    private readonly float moveSpeed;

    private int leftMoveActionID;
    private int rightMoveActionID;
    private float lastMovementSamplingTime = 0f;
    private float lastRGBDSamplingTime = 0f;
    private float currentRGBDSamplingInterval = 1.0f;

    public VRMovementRecorder(Camera userHeadCam, Transform vrHMDTransform, Logger logger,
        AoIBayesianFilter aoiFilter, float continueActionSamplingInterval,
        float minRGBDSamplingInterval, float maxRGBDSamplingInterval, float moveSpeed)
    {
        this.userHeadCam = userHeadCam;
        this.vrHMDTransform = vrHMDTransform;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
        this.minRGBDSamplingInterval = minRGBDSamplingInterval;
        this.maxRGBDSamplingInterval = maxRGBDSamplingInterval;
        this.moveSpeed = moveSpeed;
    }


    public int OnHMDMovementBegin()
    {
        var observedAction = new ObservedAction(UserActionType.Pose, IntentType.Movement,
            "User's VRHMD movement", null);
        int actionId = 0;
        if (aoiFilter.GetIntentConfidence(IntentType.Movement) >= aoiFilter.aoiThreshold)
        {
            actionId = logger.LogContinuousActionBegin("Navigate", "User is moving in VR space",
                ActionTriggerSource.XRHMD, vrHMDTransform, null, null,
                ReferentType.None, ActionContextType.Virtual, LogRecordType.UBRLog);
        }
        aoiFilter.ProcessObservedAction(observedAction);
        lastMovementSamplingTime = Time.time;
        return actionId;
    }

    public int OnHMDMovementContinue(int actionId)
    {
        if (actionId == 0) return 0;
        if (Time.time - lastMovementSamplingTime < continueActionSamplingInterval) return 0;
        var observedAction = new ObservedAction(UserActionType.Pose, IntentType.Movement,
            "User's VRHMD movement continues", null);
        if (aoiFilter.GetIntentConfidence(IntentType.Movement) >= aoiFilter.aoiThreshold)
            logger.LogContinuousActionContinue(actionId);
        aoiFilter.ProcessObservedAction(observedAction);
        lastMovementSamplingTime = Time.time;
        return 0;
    }

    public int OnHMDMovementEnd(int actionId)
    {
        logger.LogContinuousActionEnd(actionId);
        return 0;
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
        logger.LogContinuousActionEnd(leftMoveActionID);
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
        logger.LogContinuousActionEnd(rightMoveActionID);
        return 0;
    }

    private void ApplyJoystickMovement(Vector2 joystickInput)
    {
        Vector3 movement = new Vector3(
            joystickInput.x * moveSpeed * Time.deltaTime, 0,
            joystickInput.y * moveSpeed * Time.deltaTime);
        userHeadCam.transform.parent.position += movement;
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
                logger.LogDiscreteAction("RGBDSample", "User uses RGBD camera dynamic sampling",
                    ActionTriggerSource.XRHMD, userHeadCam.transform, null, null,
                    ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.Context);
            }
            lastRGBDSamplingTime = Time.time;
        }
    }
}
