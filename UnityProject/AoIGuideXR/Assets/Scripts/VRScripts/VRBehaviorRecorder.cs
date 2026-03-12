using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.Serialization;
using Whisper.Utils;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class VRBehaviorRecorder : MonoBehaviour
{
    public GameObject[] glbableObjects;
    [SerializeField] private Camera userHeadCam;
    [SerializeField] private GameObject leftHand;
    [SerializeField] private GameObject rightHand;
    [SerializeField] private GameObject leftController;
    [SerializeField] private GameObject rightController;
    [SerializeField] private MicrophoneRecord microphoneRecord;
    [SerializeField] private AoIBayesianFilter aoiFilter;
    [SerializeField] private PoseTracker VRHMD;

    [Header("RGBD Dynamic Sampling Config")]
    [SerializeField] private float minRGBDSamplingInterval = 5.0f;
    [SerializeField] private float maxRGBDSamplingInterval = 10.0f;

    [Header("Hand Gesture Dynamic Sampling Config")]
    [SerializeField] private float minHandSamplingInterval = 5.0f;
    [SerializeField] private float maxHandSamplingInterval = 10.0f;

    [FormerlySerializedAs("conitnueActionSamplingInterval")]
    [SerializeField] [Tooltip("Continuous action sampling interval (seconds)")]
    private float continueActionSamplingInterval = 0.5f;

    [Header("XR Interactors")]
    public XRRayInteractor rayInteractorRight;
    public XRRayInteractor rayInteractorLeft;
    public XRDirectInteractor directInteractorLeft;
    public XRDirectInteractor directInteractorRight;

    private Logger exrLogger;
    private InputAction leftControllerInputAction;
    private InputAction rightControllerInputAction;
    private InputAction leftControllerVoiceInputAction;
    private AoIXRControllerTracker leftControllerTracker;
    private AoIXRControllerTracker rightControllerTracker;
    private AoIXRControllerTracker leftControllerVoiceTracker;
    private TransformTracker hmdMovementTracker;

    // Sub-recorders
    private VREyeTrackingRecorder eyeTrackingRecorder;
    private VRMovementRecorder movementRecorder;
    private VRInteractionRecorder interactionRecorder;
    private VRVoiceRecorder voiceRecorder;

    private void Start()
    {
        // Initialize logger
        exrLogger = Logger.Initialize(this, userHeadCam, microphoneManager: microphoneRecord, userID: "User1");
        UBRParserAndSender.Instance.SetLogger(exrLogger);

        // Initialize sub-recorders
        eyeTrackingRecorder = new VREyeTrackingRecorder(userHeadCam, exrLogger, aoiFilter, continueActionSamplingInterval);
        eyeTrackingRecorder.Initialize();

        movementRecorder = new VRMovementRecorder(userHeadCam, VRHMD.transform, exrLogger, aoiFilter,
            continueActionSamplingInterval, minRGBDSamplingInterval, maxRGBDSamplingInterval, moveSpeed: 2.0f);

        interactionRecorder = new VRInteractionRecorder(leftHand, rightHand, exrLogger, aoiFilter,
            continueActionSamplingInterval, minHandSamplingInterval, maxHandSamplingInterval,
            continueHandElapsedTime: 0.5f,
            rayInteractorLeft, rayInteractorRight, directInteractorLeft, directInteractorRight);

        voiceRecorder = new VRVoiceRecorder(userHeadCam, exrLogger, aoiFilter);

        // Setup joystick input actions
        leftControllerInputAction = new InputAction("LeftControllerPrimary2DAxis", InputActionType.Value,
            "<XRController>{LeftHand}/{Primary2DAxis}", null, null, "Vector2");
        rightControllerInputAction = new InputAction("RightControllerPrimary2DAxis", InputActionType.Value,
            "<XRController>{RightHand}/{Primary2DAxis}", null, null, "Vector2");
        leftControllerInputAction.Enable();
        rightControllerInputAction.Enable();

        // Setup voice trigger input action
        leftControllerVoiceInputAction = new InputAction("LeftControllerTrigger",
            InputActionType.PassThrough, "<XRController>{LeftHand}/trigger");
        leftControllerVoiceInputAction.Enable();

        // Create and start controller trackers
        leftControllerTracker = new AoIXRControllerTracker(this, leftControllerInputAction,
            movementRecorder.OnLeftJoystickStart, movementRecorder.OnLeftJoystickHeld, movementRecorder.OnLeftJoystickEnd);
        rightControllerTracker = new AoIXRControllerTracker(this, rightControllerInputAction,
            movementRecorder.OnRightJoystickStart, movementRecorder.OnRightJoystickHeld, movementRecorder.OnRightJoystickEnd);
        leftControllerVoiceTracker = new AoIXRControllerTracker(this, leftControllerVoiceInputAction,
            voiceRecorder.OnTriggerPressed, voiceRecorder.OnTriggerHeld, voiceRecorder.OnTriggerReleased);

        leftControllerTracker.Start();
        rightControllerTracker.Start();
        leftControllerVoiceTracker.Start();

        // Create and start HMD movement tracker
        hmdMovementTracker = new TransformTracker(this, VRHMD.transform,
            movementRecorder.OnHMDMovementBegin, movementRecorder.OnHMDMovementContinue, movementRecorder.OnHMDMovementEnd,
            continueActionSamplingInterval, 0.075f, 10f);
        hmdMovementTracker.Start();

        interactionRecorder.SubscribeToAllGrabbables();
    }

    private void Update()
    {
        movementRecorder.SampleRGBD();
        interactionRecorder.SampleHand();
    }

    private void OnDisable() => interactionRecorder?.UnsubscribeFromAllGrabbables();

    public void GLBSaver(string filePath)
    {
        foreach (GameObject obj in glbableObjects)
        {
            var fileName = $"{obj.name}.glb";
            var dataSaveFilePath = Path.Combine(filePath, fileName);
            exrLogger.objectManager.SaveGLB(obj, dataSaveFilePath);
        }
    }
}
