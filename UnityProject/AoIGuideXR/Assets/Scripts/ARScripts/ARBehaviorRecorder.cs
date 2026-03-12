using UnityEngine;
using UnityEngine.XR.ARFoundation;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class ARBehaviorRecorder : MonoBehaviour
{
    [SerializeField] private Camera userHeadCam;
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private AROcclusionManager occlusionManager;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private AoIBayesianFilter aoiFilter;

    [Header("AR Prefabs")]
    [SerializeField] private GameObject arMarkerPrefab;
    [SerializeField] private GameObject unityMarkerPrefab;
    [SerializeField] private GameObject arMemoPrefab;

    [Header("Tracking Config")]
    [SerializeField] private float continueActionSamplingInterval = 1.0f;
    [SerializeField] private float posSensitivity = 0.20f;
    [SerializeField] private float rotSensitivity = 25f;

    [Header("RGBD Dynamic Sampling Config")]
    [SerializeField] private float minRGBDSamplingInterval = 5.0f;
    [SerializeField] private float maxRGBDSamplingInterval = 10.0f;

    private Logger exrLogger;
    private TransformTracker deviceMovementTracker;

    // Sub-recorders
    private ARMovementRecorder movementRecorder;
    private ARInteractionRecorder interactionRecorder;

    private void Start()
    {
        exrLogger = Logger.Initialize(this, userHeadCam, arCameraManager, occlusionManager,
            microphoneManager: null, userID: "User1");
        UBRParserAndSender.Instance.SetLogger(exrLogger);

        // Initialize sub-recorders
        movementRecorder = new ARMovementRecorder(userHeadCam, exrLogger, aoiFilter,
            continueActionSamplingInterval, minRGBDSamplingInterval, maxRGBDSamplingInterval);
        interactionRecorder = new ARInteractionRecorder(userHeadCam, exrLogger, aoiFilter,
            raycastManager, arMarkerPrefab, unityMarkerPrefab, arMemoPrefab);

        // Start device movement tracker
        deviceMovementTracker = new TransformTracker(this, userHeadCam.transform,
            movementRecorder.OnDeviceMovementBegin,
            movementRecorder.OnDeviceMovementContinue,
            movementRecorder.OnDeviceMovementEnd,
            continueActionSamplingInterval, posSensitivity, rotSensitivity);
        deviceMovementTracker.Start();
    }

    private void Update()
    {
        movementRecorder.SampleRGBD();
    }

    // AR touch interaction delegates (for UI button binding)
    public void OnPhysicalSurfaceARRayHitButtonPress() =>
        interactionRecorder.OnPhysicalSurfaceARRayHit();

    public void OnVirtualObjectUnityRayHitButtonPress() =>
        interactionRecorder.OnVirtualObjectUnityRayHit();

    public void OnFloatingARMemoGenerateButtonPress() =>
        interactionRecorder.OnFloatingARMemoGenerate();

    private void OnDisable()
    {
        deviceMovementTracker?.Stop();
    }

#if UNITY_EDITOR
    private void OnDestroy()
    {
        exrLogger?.SaveXRLogFile();
    }
#else
    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
            exrLogger?.SaveXRLogFile();
        else
            exrLogger?.TryResumeLogSession();
    }
#endif
}
