using System;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VRInteractionRecorder
{
    private enum HandSide { Unknown, Left, Right }

    private const float ObjectTranslationThresholdMeters = 0.01f;
    private const float ObjectRotationThresholdDegrees = 2f;
    private readonly GameObject leftHand;
    private readonly GameObject rightHand;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private readonly float continueActionSamplingInterval;
    private readonly float bayesianUpdateInterval;
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
    private string leftGrabBehaviorId = string.Empty;
    private string rightGrabBehaviorId = string.Empty;
    private GameObject leftHandGrabbedObject = null;
    private GameObject rightHandGrabbedObject = null;
    private float lastLeftGrabSamplingTime = 0f;
    private float lastRightGrabSamplingTime = 0f;
    private float lastLeftGrabUploadTime = 0f;
    private float lastRightGrabUploadTime = 0f;
    private float leftGrabStartTime = 0f;
    private float rightGrabStartTime = 0f;
    private Vector3 lastLeftGrabPosition;
    private Vector3 lastRightGrabPosition;
    private Quaternion lastLeftGrabRotation = Quaternion.identity;
    private Quaternion lastRightGrabRotation = Quaternion.identity;
    private float accumulatedLeftTranslation;
    private float accumulatedRightTranslation;
    private float accumulatedLeftRotation;
    private float accumulatedRightRotation;
    private float lastHandSamplingTime = 0f;
    private float currentHandSamplingInterval = 1.0f;

    public VRInteractionRecorder(GameObject leftHand, GameObject rightHand,
        Logger logger, AoIBayesianFilter aoiFilter,
        float continueActionSamplingInterval, float bayesianUpdateInterval,
        float minHandSamplingInterval,
        float maxHandSamplingInterval, float continueHandElapsedTime,
        XRRayInteractor rayInteractorLeft, XRRayInteractor rayInteractorRight,
        XRDirectInteractor directInteractorLeft, XRDirectInteractor directInteractorRight)
    {
        this.leftHand = leftHand;
        this.rightHand = rightHand;
        this.logger = logger;
        this.aoiFilter = aoiFilter;
        this.continueActionSamplingInterval = continueActionSamplingInterval;
        this.bayesianUpdateInterval = Mathf.Clamp(bayesianUpdateInterval, 0.01f, 0.05f);
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
        allGrabbables = UnityEngine.Object.FindObjectsOfType<XRGrabInteractable>();
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
        string behaviorId = $"vr-grab-{(isLeftHand ? "left" : "right")}-{Guid.NewGuid():N}";
        float now = Time.unscaledTime;
        if (isLeftHand)
        {
            leftGrabBehaviorId = behaviorId;
            leftGrabStartTime = now;
            if (target != null)
            {
                lastLeftGrabPosition = target.transform.position;
                lastLeftGrabRotation = target.transform.rotation;
            }
            accumulatedLeftTranslation = 0f;
            accumulatedLeftRotation = 0f;
        }
        else
        {
            rightGrabBehaviorId = behaviorId;
            rightGrabStartTime = now;
            if (target != null)
            {
                lastRightGrabPosition = target.transform.position;
                lastRightGrabRotation = target.transform.rotation;
            }
            accumulatedRightTranslation = 0f;
            accumulatedRightRotation = 0f;
        }
        var grabStart = new ObservedAction(ActionEvidenceType.GrabStart,
            $"{(isLeftHand ? "Left" : "Right")} Hand {grabMethod}", target, 0,
            behaviorId, BehaviorEventPhase.Begin, TriggerModality.Controller,
            CurrentBehaviorState.Attached, IntentType.Interaction,
            BehaviorTemporalType.Hybrid);
        grabStart.sequenceNumber = 0;
        grabStart.elapsedSeconds = 0f;
        grabStart.intervalSeconds = 0f;
        grabStart.targetRelevant = AoIBayesianFilter.ResolveTargetRelevance(target);
        grabStart.attached = EvidenceValue.True;
        grabStart.motionObserved = EvidenceValue.Unknown;
        AoIDecision decision = aoiFilter.ObserveEvidence(grabStart);
        if (!decision.shouldUpload) return 0;
        int actionId = StartGrabUpload(target, isLeftHand, handTransform, grabMethod,
            captureReferent: !decision.provisional);
        RegisterAoIFragment(actionId, grabStart, decision);
        return actionId;
    }

    // End a grab action
    public void LogGrabEnd(int actionId, GameObject target = null, bool isLeftHand = true)
    {
        if (isLeftHand && string.IsNullOrEmpty(leftGrabBehaviorId)
            && !string.IsNullOrEmpty(rightGrabBehaviorId))
            isLeftHand = false;
        string behaviorId = isLeftHand ? leftGrabBehaviorId : rightGrabBehaviorId;
        float startTime = isLeftHand ? leftGrabStartTime : rightGrabStartTime;
        float previousSampleTime = isLeftHand
            ? lastLeftGrabSamplingTime : lastRightGrabSamplingTime;
        float interval = previousSampleTime > 0f
            ? Mathf.Max(0f, Time.unscaledTime - previousSampleTime) : 0f;
        float translation = isLeftHand
            ? accumulatedLeftTranslation : accumulatedRightTranslation;
        float rotation = isLeftHand
            ? accumulatedLeftRotation : accumulatedRightRotation;
        bool moved = translation >= ObjectTranslationThresholdMeters
            || rotation >= ObjectRotationThresholdDegrees;
        var release = new ObservedAction(ActionEvidenceType.Release,
            "User released grabbed object", target, 0, behaviorId,
            BehaviorEventPhase.End, TriggerModality.Controller,
            CurrentBehaviorState.Active, IntentType.Interaction,
            BehaviorTemporalType.Hybrid);
        release.elapsedSeconds = Mathf.Max(0f, Time.unscaledTime - startTime);
        release.intervalSeconds = interval;
        release.translationMeters = translation;
        release.rotationDegrees = rotation;
        release.linearSpeed = interval > 0f ? translation / interval : 0f;
        release.angularSpeed = interval > 0f ? rotation / interval : 0f;
        release.targetRelevant = AoIBayesianFilter.ResolveTargetRelevance(target);
        release.attached = EvidenceValue.False;
        release.motionObserved = moved ? EvidenceValue.True : EvidenceValue.False;
        release.crossModalSupport = EvidenceValue.Unknown;
        AoIDecision decision = aoiFilter.ObserveEvidence(release);
        if (isLeftHand)
        {
            leftGrabBehaviorId = string.Empty;
            accumulatedLeftTranslation = 0f;
            accumulatedLeftRotation = 0f;
        }
        else
        {
            rightGrabBehaviorId = string.Empty;
            accumulatedRightTranslation = 0f;
            accumulatedRightRotation = 0f;
        }
        if (decision.sendCancel && actionId > 0)
        {
            CancelGrabUpload(actionId);
        }
        else if (decision.shouldUpload)
        {
            if (actionId <= 0)
            {
                Transform handTransform = isLeftHand ? leftHand.transform : rightHand.transform;
                actionId = StartGrabUpload(target, isLeftHand, handTransform, "Grab");
                RegisterAoIFragment(actionId, release, decision, BehaviorEventPhase.Begin);
            }
            logger.LogContinuousActionEnd(actionId);
            RegisterAoIFragment(actionId, release, decision);
        }
    }

    private int StartGrabUpload(GameObject target, bool isLeftHand, Transform handTransform,
        string grabMethod, bool captureReferent = true)
    {
        string handName = isLeftHand ? "left hand" : "right hand";
        ActionTriggerSource handSource = isLeftHand ? ActionTriggerSource.XRHand_L : ActionTriggerSource.XRHand_R;
        return logger.LogContinuousActionBegin(grabMethod, $"{grabMethod} with {handName}",
            handSource, handTransform, target, target != null ? target.transform : null,
            ReferentType.Virtual, ActionContextType.Virtual, LogRecordType.UBRLog,
            captureReferent);
    }


    private void OnObjectGrabbed(SelectEnterEventArgs args)
    {
        var interactor = args.interactorObject;
        var grabbedObject = args.interactableObject.transform.gameObject;
        bool isRayGrab = interactor is XRRayInteractor;
        string grabMethod = isRayGrab ? "Ray Grab" : "Direct Grab";
        HandSide hand = ResolveInteractorHand(interactor);
        Debug.Log("[XR Grab Begin] target=" + grabbedObject.name + ", interactor="
            + GetHierarchyPath(interactor != null ? interactor.transform : null)
            + ", resolvedHand=" + hand);

        if (hand == HandSide.Left)
        {
            leftHandGrabbedObject = grabbedObject;
            lastLeftGrabSamplingTime = Time.unscaledTime;
            lastLeftGrabUploadTime = Time.unscaledTime;
            leftHandGrabActionID = LogGrabBegin(grabbedObject, true, interactor.transform, grabMethod);
        }
        else if (hand == HandSide.Right)
        {
            rightHandGrabbedObject = grabbedObject;
            lastRightGrabSamplingTime = Time.unscaledTime;
            lastRightGrabUploadTime = Time.unscaledTime;
            rightHandGrabActionID = LogGrabBegin(grabbedObject, false, interactor.transform, grabMethod);
        }
        else Debug.LogError("[XR Grab DROPPED] Could not resolve Left/Right hand for "
            + GetHierarchyPath(interactor != null ? interactor.transform : null));
    }

    private void OnObjectReleased(SelectExitEventArgs args)
    {
        var interactor = args.interactorObject;
        HandSide hand = ResolveInteractorHand(interactor);
        Debug.Log("[XR Grab End] interactor="
            + GetHierarchyPath(interactor != null ? interactor.transform : null)
            + ", resolvedHand=" + hand);
        if (hand == HandSide.Left)
        {
            // Include movement after the most recent 0.5-second Continue sample.
            AccumulateGrabMotion(true);
            LogGrabEnd(leftHandGrabActionID, leftHandGrabbedObject, true);
            leftHandGrabActionID = 0;
            leftHandGrabbedObject = null;
            lastLeftGrabSamplingTime = 0f;
            lastLeftGrabUploadTime = 0f;
        }
        else if (hand == HandSide.Right)
        {
            // Include movement after the most recent 0.5-second Continue sample.
            AccumulateGrabMotion(false);
            LogGrabEnd(rightHandGrabActionID, rightHandGrabbedObject, false);
            rightHandGrabActionID = 0;
            rightHandGrabbedObject = null;
            lastRightGrabSamplingTime = 0f;
            lastRightGrabUploadTime = 0f;
        }
        else Debug.LogError("[XR Release DROPPED] Could not resolve Left/Right hand for "
            + GetHierarchyPath(interactor != null ? interactor.transform : null));
    }


    // Dynamic hand sampling based on the AoI posterior of confirmed interactions.
    public void SampleHand()
    {
        if (aoiFilter.FilteringEnabled && !aoiFilter.HasConfirmedBehavior) return;
        if (!IsHanding()) return;

        float interactionAoIConfidence = aoiFilter.FilteringEnabled
            ? aoiFilter.GetConfirmedBehaviorAoIConfidence(IntentType.Interaction)
            : 1f;
        currentHandSamplingInterval = Mathf.Lerp(maxHandSamplingInterval,
            minHandSamplingInterval, interactionAoIConfidence);
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
        AccumulateGrabMotion(true);
        AccumulateGrabMotion(false);
        if (leftHandGrabbedObject != null
            && Time.unscaledTime - lastLeftGrabSamplingTime >= bayesianUpdateInterval)
        {
            EvaluateHeldGrab(true);
            lastLeftGrabSamplingTime = Time.unscaledTime;
        }
        if (rightHandGrabbedObject != null
            && Time.unscaledTime - lastRightGrabSamplingTime >= bayesianUpdateInterval)
        {
            EvaluateHeldGrab(false);
            lastRightGrabSamplingTime = Time.unscaledTime;
        }
    }

    private void EvaluateHeldGrab(bool isLeftHand)
    {
        ObservedAction observedAction = CreateGrabContinue(isLeftHand);
        AoIDecision decision = aoiFilter.ObserveEvidence(observedAction);
        int actionId = isLeftHand ? leftHandGrabActionID : rightHandGrabActionID;

        if (decision.sendCancel)
        {
            if (actionId > 0) CancelGrabUpload(actionId);
            if (isLeftHand) leftHandGrabActionID = 0;
            else rightHandGrabActionID = 0;
            return;
        }

        if (!decision.confirmed || decision.rejected) return;

        if (decision.newlyConfirmed && actionId > 0)
            logger.EnableContinuousActionReferent(actionId);

        GameObject target = isLeftHand ? leftHandGrabbedObject : rightHandGrabbedObject;
        Transform handTransform = isLeftHand ? leftHand.transform : rightHand.transform;
        if (actionId <= 0)
        {
            actionId = StartGrabUpload(target, isLeftHand, handTransform, "Grab Hold");
            RegisterAoIFragment(actionId, observedAction, decision, BehaviorEventPhase.Begin);
            if (isLeftHand)
            {
                leftHandGrabActionID = actionId;
                lastLeftGrabUploadTime = Time.unscaledTime;
            }
            else
            {
                rightHandGrabActionID = actionId;
                lastRightGrabUploadTime = Time.unscaledTime;
            }
            return;
        }

        float lastUpload = isLeftHand ? lastLeftGrabUploadTime : lastRightGrabUploadTime;
        if (Time.unscaledTime - lastUpload < continueActionSamplingInterval) return;
        logger.LogContinuousActionContinue(actionId);
        RegisterAoIFragment(actionId, observedAction, decision);
        if (isLeftHand) lastLeftGrabUploadTime = Time.unscaledTime;
        else lastRightGrabUploadTime = Time.unscaledTime;
    }

    private void CancelGrabUpload(int actionId)
    {
        logger.DiscardContinuousAction(actionId);
        if (UBRParserAndSender.Instance != null)
            UBRParserAndSender.Instance.CancelAoIAction(actionId);
    }

    private ObservedAction CreateGrabContinue(bool isLeftHand)
    {
        GameObject target = isLeftHand ? leftHandGrabbedObject : rightHandGrabbedObject;
        float now = Time.unscaledTime;
        float previousTime = isLeftHand ? lastLeftGrabSamplingTime : lastRightGrabSamplingTime;
        float startTime = isLeftHand ? leftGrabStartTime : rightGrabStartTime;
        float interval = Mathf.Max(0.0001f, now - previousTime);
        float translation = isLeftHand
            ? accumulatedLeftTranslation : accumulatedRightTranslation;
        float rotation = isLeftHand
            ? accumulatedLeftRotation : accumulatedRightRotation;
        bool moved = translation >= ObjectTranslationThresholdMeters
            || rotation >= ObjectRotationThresholdDegrees;

        var observation = new ObservedAction(ActionEvidenceType.GrabHold,
            $"{(isLeftHand ? "Left" : "Right")} Hand Grab Continuous",
            target, 0, isLeftHand ? leftGrabBehaviorId : rightGrabBehaviorId,
            BehaviorEventPhase.Continue, TriggerModality.Controller,
            CurrentBehaviorState.Attached, IntentType.Interaction,
            BehaviorTemporalType.Hybrid);
        observation.elapsedSeconds = Mathf.Max(0f, now - startTime);
        observation.intervalSeconds = interval;
        observation.translationMeters = translation;
        observation.rotationDegrees = rotation;
        observation.linearSpeed = translation / interval;
        observation.angularSpeed = rotation / interval;
        observation.targetRelevant = AoIBayesianFilter.ResolveTargetRelevance(target);
        observation.attached = EvidenceValue.True;
        observation.motionObserved = moved ? EvidenceValue.True : EvidenceValue.False;
        // Object motion is useful evidence, but it still comes from the same Unity
        // interaction stream. Cross-modal support is reserved for gaze/voice/etc.
        observation.crossModalSupport = EvidenceValue.Unknown;

        if (target != null)
        {
            if (isLeftHand)
            {
                lastLeftGrabPosition = target.transform.position;
                lastLeftGrabRotation = target.transform.rotation;
                accumulatedLeftTranslation = 0f;
                accumulatedLeftRotation = 0f;
            }
            else
            {
                lastRightGrabPosition = target.transform.position;
                lastRightGrabRotation = target.transform.rotation;
                accumulatedRightTranslation = 0f;
                accumulatedRightRotation = 0f;
            }
        }
        return observation;
    }

    private void AccumulateGrabMotion(bool isLeftHand)
    {
        GameObject target = isLeftHand ? leftHandGrabbedObject : rightHandGrabbedObject;
        if (target == null) return;
        Vector3 previousPosition = isLeftHand ? lastLeftGrabPosition : lastRightGrabPosition;
        Quaternion previousRotation = isLeftHand ? lastLeftGrabRotation : lastRightGrabRotation;
        float translation = Vector3.Distance(previousPosition, target.transform.position);
        float rotation = Quaternion.Angle(previousRotation, target.transform.rotation);
        if (isLeftHand)
        {
            accumulatedLeftTranslation += translation;
            accumulatedLeftRotation += rotation;
            lastLeftGrabPosition = target.transform.position;
            lastLeftGrabRotation = target.transform.rotation;
        }
        else
        {
            accumulatedRightTranslation += translation;
            accumulatedRightRotation += rotation;
            lastRightGrabPosition = target.transform.position;
            lastRightGrabRotation = target.transform.rotation;
        }
    }

    private static void RegisterAoIFragment(int actionId, ObservedAction action,
        AoIDecision decision, BehaviorEventPhase? transmittedPhase = null)
    {
        AoIUBRMetadataBridge.Register(actionId, action, decision, transmittedPhase);
    }

    private bool IsHanding() => leftHand.activeSelf && rightHand.activeSelf;


    private HandSide ResolveInteractorHand(IXRSelectInteractor interactor)
    {
        if (interactor == null) return HandSide.Unknown;
        Transform value = interactor.transform;
        if ((rayInteractorLeft != null && value == rayInteractorLeft.transform)
            || (directInteractorLeft != null && value == directInteractorLeft.transform))
            return HandSide.Left;
        if ((rayInteractorRight != null && value == rayInteractorRight.transform)
            || (directInteractorRight != null && value == directInteractorRight.transform))
            return HandSide.Right;

        // Runtime interactors may share the generic name "Ray Interactor".
        // Their handedness is carried by an ancestor such as Left Hand,
        // Right Hand, Left Controller, or Right Controller.
        for (Transform current = value; current != null; current = current.parent)
        {
            string lower = current.name.ToLowerInvariant();
            if (lower.Contains("left hand") || lower.Contains("lefthand")
                || lower.Contains("left controller") || lower.Contains("leftcontroller"))
                return HandSide.Left;
            if (lower.Contains("right hand") || lower.Contains("righthand")
                || lower.Contains("right controller") || lower.Contains("rightcontroller"))
                return HandSide.Right;
        }
        return HandSide.Unknown;
    }

    private static string GetHierarchyPath(Transform value)
    {
        if (value == null) return "<null>";
        string path = value.name;
        for (Transform current = value.parent; current != null; current = current.parent)
            path = current.name + "/" + path;
        return path;
    }
}
