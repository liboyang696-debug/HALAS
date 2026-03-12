using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

// Handles AR touch-based interactions: physical surface raycast, virtual object raycast, and memo placement
public class ARInteractionRecorder
{
    private readonly Camera userHeadCam;
    private readonly Logger logger;
    private readonly AoIBayesianFilter aoiFilter;
    private readonly ARRaycastManager raycastManager;
    private readonly GameObject arMarkerPrefab;
    private readonly GameObject unityMarkerPrefab;
    private readonly GameObject arMemoPrefab;

    public ARInteractionRecorder(Camera cam, Logger logger, AoIBayesianFilter filter,
        ARRaycastManager raycastMgr, GameObject arMarker, GameObject unityMarker, GameObject arMemo)
    {
        userHeadCam = cam;
        this.logger = logger;
        aoiFilter = filter;
        raycastManager = raycastMgr;
        arMarkerPrefab = arMarker;
        unityMarkerPrefab = unityMarker;
        arMemoPrefab = arMemo;
    }

    // Place marker on detected physical surface via AR raycast
    public void OnPhysicalSurfaceARRayHit()
    {
        var camTransform = userHeadCam.transform;
        var ray = new Ray(camTransform.position, camTransform.forward);
        var hitResults = new List<ARRaycastHit>();

        if (!raycastManager.Raycast(ray, hitResults,
            UnityEngine.XR.ARSubsystems.TrackableType.PlaneWithinBounds))
            return;

        var hitPose = hitResults[0].pose;
        var markerObj = Object.Instantiate(arMarkerPrefab, hitPose.position, Quaternion.identity);

        var observedAction = new ObservedAction(UserActionType.Gesture, IntentType.Interaction,
            "Place physical marker via AR ray", markerObj);
        aoiFilter?.ProcessObservedAction(observedAction);

        logger.LogDiscreteAction("Place Marker",
            "Marking walkable paths in user's physical environment",
            ActionTriggerSource.XRHMD, camTransform,
            markerObj, markerObj.transform,
            ReferentType.Physical, ActionContextType.Physical);
    }

    // Inspect virtual object via Unity Physics raycast
    public void OnVirtualObjectUnityRayHit()
    {
        var camTransform = userHeadCam.transform;
        var ray = new Ray(camTransform.position, camTransform.forward);
        var hits = Physics.RaycastAll(ray, 50f, LayerMask.GetMask("AR Ray Object"));

        if (hits.Length == 0) return;

        var hitObj = hits[0].collider.gameObject;
        var indicator = Object.Instantiate(unityMarkerPrefab);
        indicator.transform.position = hits[0].point;
        Object.Destroy(indicator, 1.5f);

        var observedAction = new ObservedAction(UserActionType.Gesture, IntentType.Interaction,
            "Inspect virtual object via ray", hitObj);
        aoiFilter?.ProcessObservedAction(observedAction);

        logger.LogDiscreteAction("Inspection Check",
            "Placing an additional indicator on an existing virtual marker",
            ActionTriggerSource.XRHMD, camTransform,
            hitObj, hitObj.transform,
            ReferentType.Virtual, ActionContextType.Physical);
    }

    // Generate floating AR memo anchored at device's current position
    public void OnFloatingARMemoGenerate()
    {
        var camTransform = userHeadCam.transform;
        var memoObj = Object.Instantiate(arMemoPrefab);
        memoObj.transform.position = camTransform.position + camTransform.forward * 0.5f;
        memoObj.transform.rotation = camTransform.rotation;
        memoObj.AddComponent<ARAnchor>();

        var observedAction = new ObservedAction(UserActionType.Gesture, IntentType.Interaction,
            "Generate floating AR memo", memoObj);
        aoiFilter?.ProcessObservedAction(observedAction);

        logger.LogDiscreteAction("Generate Memo",
            "Situating floating virtual marker at AR device's location",
            ActionTriggerSource.ARHandHeld, camTransform,
            memoObj, memoObj.transform,
            ReferentType.Virtual, ActionContextType.Physical);
    }
}
