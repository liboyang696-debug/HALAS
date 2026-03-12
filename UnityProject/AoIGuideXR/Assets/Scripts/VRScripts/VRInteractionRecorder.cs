using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VRInteractionRecorder
{
    private readonly GameObject leftHand;
    private readonly GameObject rightHand;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private readonly float continueActionSamplingInterval;
    private readonly float minHandSamplingInterval;
    private readonly float maxHandSamplingInterval;
    private readonly float continueHandElapsedTime;
    private readonly XRRayInteractor rayInteractorLeft;
    private readonly XRRayInteractor rayInteractorRight;
    private readonly XRDirectInteractor directInteractorLeft;
    private readonly XRDirectInteractor directInteractorRight;

    private XRGrabInteractable[] allGrabbables;
    private int leftHandGrabActionID;
    private int rightHandGrabActionID;
    private GameObject leftHandGrabbedObject = null;
    private GameObject rightHandGrabbedObject = null;
    private float lastLeftGrabSamplingTime = 0f;
    private float lastRightGrabSamplingTime = 0f;
    private float lastHandSamplingTime = 0f;
    private float currentHandSamplingInterval = 1.0f;
    private float lastContinueHandTime = 0f;

    public VRInteractionRecorder(GameObject leftHand, GameObject rightHand,
        Logger logger, AoIBayesianFilter aoiFilter,
        float continueActionSamplingInterval, float minHandSamplingInterval,
        float maxHandSamplingInterval, float continueHandElapsedTime,
        XRRayInteractor rayInteractorLeft, XRRayInteractor rayInteractorRight,
        XRDirectInteractor directInteractorLeft, XRDirectInteractor directInteractorRight)
    {
        this.leftHand = leftHand;
        this.rightHand = rightHand;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
        this.minHandSamplingInterval = minHandSamplingInterval;
        this.maxHandSamplingInterval = maxHandSamplingInterval;
        this.continueHandElapsedTime = continueHandElapsedTime;
        this.rayInteractorLeft = rayInteractorLeft;
        this.rayInteractorRight = rayInteractorRight;
        this.directInteractorLeft = directInteractorLeft;
        this.directInteractorRight = directInteractorRight;
    }


    public void SubscribeToAllGrabbables()
    {
        allGrabbables = Object.FindObjectsOfType<XRGrabInteractable>();
        foreach (var grabbable in allGrabbables)
        {
            grabbable.selectEntered.AddListener(OnObjectGrabbed);
            grabbable.selectExited.AddListener(OnObjectReleased);
        }
    }

    public void UnsubscribeFromAllGrabbables()
    {
        if (allGrabbables == null) return;
        foreach (var grabbable in allGrabbables)
        {
            if (grabbable != null)
            {
                grabbable.selectEntered.RemoveListener(OnObjectGrabbed);
                grabbable.selectExited.RemoveListener(OnObjectReleased);
            }
        }
    }


    // Begin logging a grab action. Returns actionID (0 if filtered by AoI).
    public int LogGrabBegin(GameObject target, bool isLeftHand, Transform handTransform,
        string grabMethod = "Direct Grab")
    {
        string handName = isLeftHand ? "left hand" : "right hand";
        ActionTriggerSource handSource = isLeftHand ? ActionTriggerSource.XRHand_L : ActionTriggerSource.XRHand_R;

        var observedAction = new ObservedAction(UserActionType.Gesture, IntentType.Interaction,
            $"{(isLeftHand ? "Left" : "Right")} Hand {grabMethod}", target);
        int actionId = 0;
        if (aoiFilter.GetIntentConfidence(IntentType.Interaction) >= aoiFilter.aoiThreshold)
        {
            actionId = logger.LogContinuousActionBegin(grabMethod, $"{grabMethod} with {handName}",
                handSource, handTransform, target, target.transform,
                ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.UBRLog);
        }
        aoiFilter.ProcessObservedAction(observedAction);
        return actionId;
    }

    // End a grab action
    public void LogGrabEnd(int actionId)
    {
        if (actionId == 0) return;
        logger.LogContinuousActionEnd(actionId);
    }


    private void OnObjectGrabbed(SelectEnterEventArgs args)
    {
        var interactor = args.interactorObject;
        var grabbedObject = args.interactableObject.transform.gameObject;
        bool isRayGrab = interactor is XRRayInteractor;
        string grabMethod = isRayGrab ? "Ray Grab" : "Direct Grab";

        if (IsLeftHandInteractor(interactor))
        {
            leftHandGrabbedObject = grabbedObject;
            lastLeftGrabSamplingTime = Time.time;
            leftHandGrabActionID = LogGrabBegin(grabbedObject, true, interactor.transform, grabMethod);
        }
        else if (IsRightHandInteractor(interactor))
        {
            rightHandGrabbedObject = grabbedObject;
            lastRightGrabSamplingTime = Time.time;
            rightHandGrabActionID = LogGrabBegin(grabbedObject, false, interactor.transform, grabMethod);
        }
    }

    private void OnObjectReleased(SelectExitEventArgs args)
    {
        var interactor = args.interactorObject;
        if (IsLeftHandInteractor(interactor))
        {
            LogGrabEnd(leftHandGrabActionID);
            leftHandGrabbedObject = null;
            lastLeftGrabSamplingTime = 0f;
        }
        else if (IsRightHandInteractor(interactor))
        {
            LogGrabEnd(rightHandGrabActionID);
            rightHandGrabbedObject = null;
            lastRightGrabSamplingTime = 0f;
        }
    }


    // Adaptive hand gesture sampling based on gesture probability from AoI filter
    public void SampleHand()
    {
        if (!IsHanding()) return;

        ContinueHanding();
        float handProbability = aoiFilter.CalculateMarginalProbability(UserActionType.Gesture);
        currentHandSamplingInterval = Mathf.Lerp(maxHandSamplingInterval, minHandSamplingInterval, handProbability);
        if (Time.time - lastHandSamplingTime >= currentHandSamplingInterval)
        {
            if (logger?.imageRecordManager != null)
            {
                logger.LogDiscreteAction("GestureSample",
                    "User uses gesture sampling for dynamic sampling of the camera",
                    ActionTriggerSource.XRHand_L, leftHand.transform, null, null,
                    ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.Hand);
            }
            lastHandSamplingTime = Time.time;
        }
    }

    public void SampleGrabHeld()
    {
        if (leftHandGrabbedObject != null
            && Time.time - lastLeftGrabSamplingTime >= continueActionSamplingInterval)
        {
            var observedAction = new ObservedAction(UserActionType.Gesture, IntentType.Interaction,
                "Left Hand Grab Continuous", leftHandGrabbedObject);
            if (aoiFilter.GetIntentConfidence(IntentType.Interaction) >= aoiFilter.aoiThreshold)
                logger.LogContinuousActionContinue(leftHandGrabActionID);
            aoiFilter.ProcessObservedAction(observedAction);
            lastLeftGrabSamplingTime = Time.time;
        }
        if (rightHandGrabbedObject != null
            && Time.time - lastRightGrabSamplingTime >= continueActionSamplingInterval)
        {
            var observedAction = new ObservedAction(UserActionType.Gesture, IntentType.Interaction,
                "Right Hand Grab Continuous", rightHandGrabbedObject);
            if (aoiFilter.GetIntentConfidence(IntentType.Interaction) >= aoiFilter.aoiThreshold)
                logger.LogContinuousActionContinue(rightHandGrabActionID);
            aoiFilter.ProcessObservedAction(observedAction);
            lastRightGrabSamplingTime = Time.time;
        }
    }

    private void ContinueHanding()
    {
        if (Time.time - lastContinueHandTime < continueHandElapsedTime) return;
        var observedAction = new ObservedAction(UserActionType.Gesture, IntentType.Interaction,
            "User is waving their hands to try to interact with something", null);
        aoiFilter.ProcessObservedAction(observedAction);
        lastContinueHandTime = Time.time;
    }

    private bool IsHanding() => leftHand.activeSelf && rightHand.activeSelf;


    private bool IsLeftHandInteractor(IXRSelectInteractor interactor)
    {
        if (interactor == null) return false;
        if (rayInteractorLeft != null && interactor.transform == rayInteractorLeft.transform) return true;
        if (directInteractorLeft != null && interactor.transform == directInteractorLeft.transform) return true;
        Debug.Log($"Left Interactor Name: {interactor.transform.name}");
        return interactor.transform.name.ToLower().Contains("left");
    }

    private bool IsRightHandInteractor(IXRSelectInteractor interactor)
    {
        if (interactor == null) return false;
        if (rayInteractorRight != null && interactor.transform == rayInteractorRight.transform) return true;
        if (directInteractorRight != null && interactor.transform == directInteractorRight.transform) return true;
        Debug.Log($"Right Interactor Name: {interactor.transform.name}");
        return interactor.transform.name.ToLower().Contains("right");
    }
}
