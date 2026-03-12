using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ExplainableXR;
using Whisper.Utils;
using Logger = ExplainableXR.Logger;

public class VRBehaviorRecorderAutoTest : MonoBehaviour
{
    [Header("Referenced Components")]
    [SerializeField] private Camera userHeadCam;
    [SerializeField] private MicrophoneRecord microphoneRecord;
    [SerializeField] private AoIBayesianFilter aoiFilter;
    [SerializeField] private Transform vrHMD;
    [SerializeField] private Transform vrRig;
    [SerializeField] private Transform leftHandTransform;
    [SerializeField] private Transform rightHandTransform;

    [Header("Experiment Objects")]
    [SerializeField] private GameObject[] experimentObjects;
    [SerializeField] private Transform experimentTableCenter;

    [Header("Phase Duration")]
    [SerializeField] private float phase1_RandomMove = 30f;
    [SerializeField] private float phase2_Interaction = 60f;
    [SerializeField] private float phase3_RandomMove = 30f;
    [SerializeField] private float phase4_RandomGaze = 60f;
    [SerializeField] private float phase5_RandomMove = 30f;
    [SerializeField] private float phase6_Interaction = 60f;
    [SerializeField] private float phase7_RandomMove = 30f;

    [Header("Simulation")]
    [SerializeField] private float moveSpeed = 0.5f;

    private Logger exrLogger;
    private VREyeTrackingRecorder eyeTrackingRecorder;
    private VRMovementRecorder movementRecorder;
    private VRInteractionRecorder interactionRecorder;

    private Vector3 initialPosition;
    private Quaternion initialRotation;
    private int lastGrabActionId;
    private List<ActionLatencyRecord> latencyRecords = new List<ActionLatencyRecord>();
    private Dictionary<int, ActionLatencyRecord> actionIdToRecord = new Dictionary<int, ActionLatencyRecord>();

    private class ActionLatencyRecord
    {
        public string actionName;
        public UserActionType actionType;
        public IntentType intentType;
        public int actionId;
        public float actionStartTime;
        public float actionEndTime;
        public bool wasFiltered;
        public float ActionRecordLatency => (actionEndTime - actionStartTime) * 1000;

        public override string ToString()
        {
            return $"{actionName},{actionType},{intentType},{actionId}," +
                   $"{actionStartTime:F3},{actionEndTime:F3}," +
                   $"{ActionRecordLatency:F2},{wasFiltered}";
        }
    }

    private void Start()
    {
        initialPosition = vrRig.position;
        initialRotation = vrRig.rotation;

        exrLogger = Logger.Initialize(this, userHeadCam, microphoneManager: microphoneRecord, userID: "VRTestUser");
        UBRParserAndSender.Instance.SetLogger(exrLogger);

        eyeTrackingRecorder = new VREyeTrackingRecorder(userHeadCam, exrLogger, aoiFilter, 0.3f);
        movementRecorder = new VRMovementRecorder(userHeadCam, vrHMD, exrLogger, aoiFilter,
            0.5f, 5f, 10f, moveSpeed);
        interactionRecorder = new VRInteractionRecorder(
            leftHandTransform.gameObject, rightHandTransform.gameObject,
            exrLogger, aoiFilter, 0.5f, 5f, 10f, 0.5f,
            null, null, null, null);
        StartCoroutine(RunFullTestSequence());
    }

    private IEnumerator RunFullTestSequence()
    {
        Debug.Log("<color=yellow>[Phase 1/7] Random Move 30s</color>");
        yield return StartCoroutine(Phase_RandomMovement(phase1_RandomMove, "Phase1"));

        Debug.Log("<color=green>[Phase 2/7] Interaction 60s</color>");
        yield return StartCoroutine(Phase_Interaction(phase2_Interaction, "Phase2"));

        Debug.Log("<color=yellow>[Phase 3/7] Random Move 30s</color>");
        yield return StartCoroutine(Phase_RandomMovement(phase3_RandomMove, "Phase3"));

        Debug.Log("<color=cyan>[Phase 4/7] Random Gaze 60s</color>");
        yield return StartCoroutine(Phase_RandomGaze(phase4_RandomGaze));

        Debug.Log("<color=yellow>[Phase 5/7] Random Move 30s</color>");
        yield return StartCoroutine(Phase_RandomMovement(phase5_RandomMove, "Phase5"));

        Debug.Log("<color=green>[Phase 6/7] Interaction 60s</color>");
        yield return StartCoroutine(Phase_Interaction(phase6_Interaction, "Phase6"));

        Debug.Log("<color=yellow>[Phase 7/7] Random Move 30s</color>");
        yield return StartCoroutine(Phase_RandomMovement(phase7_RandomMove, "Phase7"));

        if (actionIdToRecord.Count > 0)
        {
            Debug.LogWarning($"Force completing {actionIdToRecord.Count} pending actions");
            foreach (var kvp in actionIdToRecord)
            {
                kvp.Value.actionEndTime = Time.time;
                latencyRecords.Add(kvp.Value);
            }
            actionIdToRecord.Clear();
        }
    }

    private void StartRecordingLatency(string actionName, UserActionType actionType,
        IntentType intentType, int actionId, bool wasFiltered)
    {
        if (actionId <= 0) return;
        actionIdToRecord[actionId] = new ActionLatencyRecord
        {
            actionName = actionName,
            actionType = actionType,
            intentType = intentType,
            actionId = actionId,
            actionStartTime = Time.time,
            wasFiltered = wasFiltered
        };
    }

    private void EndRecordingLatency(int actionId)
    {
        if (!actionIdToRecord.ContainsKey(actionId)) return;
        var record = actionIdToRecord[actionId];
        record.actionEndTime = Time.time;
        latencyRecords.Add(record);
        actionIdToRecord.Remove(actionId);
    }

    private IEnumerator Phase_RandomMovement(float phaseDuration, string phaseName)
    {
        float phaseStartTime = Time.time;
        int moveCount = 0;

        while (Time.time - phaseStartTime < phaseDuration)
        {
            moveCount++;
            yield return StartCoroutine(SimulateRandomHeadMovement(0.8f));

            int actionId = movementRecorder.OnHMDMovementBegin();
            if (actionId > 0)
            {
                StartRecordingLatency($"{phaseName}_Move_{moveCount}",
                    UserActionType.Pose, IntentType.Movement, actionId, false);
                yield return new WaitForSeconds(0.2f);
                movementRecorder.OnHMDMovementEnd(actionId);
                EndRecordingLatency(actionId);
            }

            yield return StartCoroutine(SimulateRandomBodyMovement(0.6f));
            yield return new WaitForSeconds(0.2f);
        }

        Debug.Log($"<color=yellow>{phaseName} Done - {moveCount} moves, " +
                  $"Movement: {aoiFilter.GetIntentConfidence(IntentType.Movement):F3}</color>");
    }

    private IEnumerator SimulateRandomHeadMovement(float duration)
    {
        float elapsed = 0f;
        Vector3 startRot = vrHMD.localEulerAngles;
        Vector3 targetRot = startRot + new Vector3(
            Random.Range(-15f, 15f), Random.Range(-30f, 30f), Random.Range(-5f, 5f));

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            vrHMD.localEulerAngles = Vector3.Lerp(startRot, targetRot, elapsed / duration);
            yield return null;
        }
    }

    private IEnumerator SimulateRandomBodyMovement(float duration)
    {
        float elapsed = 0f;
        Vector3 startPos = vrRig.position;
        Vector3 targetPos = startPos + new Vector3(
            Random.Range(-0.3f, 0.3f), 0, Random.Range(-0.3f, 0.3f));

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            vrRig.position = Vector3.Lerp(startPos, targetPos, elapsed / duration);
            yield return null;
        }
    }

    private IEnumerator Phase_Interaction(float phaseDuration, string phaseName)
    {
        if (experimentObjects == null || experimentObjects.Length == 0)
        {
            Debug.LogWarning("[Test] No experiment objects configured");
            yield return new WaitForSeconds(phaseDuration);
            yield break;
        }

        float phaseStartTime = Time.time;
        int interactionCount = 0;

        while (Time.time - phaseStartTime < phaseDuration)
        {
            for (int i = 0; i < experimentObjects.Length; i++)
            {
                if (Time.time - phaseStartTime >= phaseDuration) break;
                GameObject target = experimentObjects[i];
                if (target == null) continue;

                bool useLeftHand = i % 2 == 0;
                yield return StartCoroutine(GrabObject(target, useLeftHand, 1.2f));
                yield return StartCoroutine(MoveGrabbedObject(useLeftHand, 1f));
                yield return StartCoroutine(ReleaseObject());
                interactionCount++;
                yield return new WaitForSeconds(0.3f);
            }
        }

        Debug.Log($"<color=green>[{phaseName}] Interaction Done - {interactionCount} interactions, " +
                  $"Interaction: {aoiFilter.GetIntentConfidence(IntentType.Interaction):F3}</color>");
    }

    private IEnumerator Phase_RandomGaze(float phaseDuration)
    {
        if (experimentObjects == null || experimentObjects.Length == 0)
        {
            Debug.LogWarning("[Test] No experiment objects to gaze at");
            yield return new WaitForSeconds(phaseDuration);
            yield break;
        }

        float phaseStartTime = Time.time;
        int gazeCount = 0;
        List<GameObject> availableTargets = new List<GameObject>();
        foreach (var obj in experimentObjects)
        {
            if (obj != null) availableTargets.Add(obj);
        }

        while (Time.time - phaseStartTime < phaseDuration)
        {
            if (availableTargets.Count == 0) break;
            int randomIndex = Random.Range(0, availableTargets.Count);
            GameObject target = availableTargets[randomIndex];

            float gazeDuration = Random.Range(1.0f, 2.5f);
            yield return StartCoroutine(GazeAtObject(target, gazeDuration));
            gazeCount++;
            yield return new WaitForSeconds(Random.Range(0.1f, 0.4f));
        }

        Debug.Log($"<color=cyan>[Phase4] Random Gaze Done - {gazeCount} gazes, " +
                  $"EyeTracking: {aoiFilter.GetIntentConfidence(IntentType.EyeTracking):F3}</color>");
    }

    private IEnumerator GazeAtObject(GameObject target, float gazeDuration)
    {
        Vector3 direction = (target.transform.position - vrHMD.position).normalized;
        Quaternion startRot = vrHMD.rotation;
        Quaternion targetRot = Quaternion.LookRotation(direction);
        float elapsed = 0f;

        while (elapsed < 0.4f)
        {
            elapsed += Time.deltaTime;
            vrHMD.rotation = Quaternion.Slerp(startRot, targetRot, Mathf.SmoothStep(0, 1, elapsed / 0.4f));
            yield return null;
        }

        int gazeActionId = eyeTrackingRecorder.LogGazeBegin(target);
        StartRecordingLatency($"Gaze_{target.name}",
            UserActionType.EyeTracking, IntentType.EyeTracking, gazeActionId, false);

        float gazeElapsed = 0f;
        int continueCount = 0;
        while (gazeElapsed < gazeDuration)
        {
            gazeElapsed += Time.deltaTime;
            if (gazeElapsed > continueCount * 0.3f && gazeActionId > 0)
            {
                eyeTrackingRecorder.LogGazeContinue(gazeActionId, target);
                continueCount++;
            }
            yield return null;
        }

        if (gazeActionId > 0)
        {
            eyeTrackingRecorder.LogGazeEnd(gazeActionId);
            EndRecordingLatency(gazeActionId);
        }
    }

    private IEnumerator GrabObject(GameObject target, bool useLeftHand, float duration)
    {
        Transform handTransform = useLeftHand ? leftHandTransform : rightHandTransform;

        float elapsed = 0f;
        Vector3 startPos = handTransform.position;
        Vector3 targetPos = target.transform.position;
        while (elapsed < duration * 0.6f)
        {
            elapsed += Time.deltaTime;
            handTransform.position = Vector3.Lerp(startPos, targetPos, elapsed / (duration * 0.6f));
            yield return null;
        }

        lastGrabActionId = interactionRecorder.LogGrabBegin(target, useLeftHand, handTransform);
        StartRecordingLatency($"Grab_{target.name}",
            UserActionType.Gesture, IntentType.Interaction, lastGrabActionId, false);

        yield return new WaitForSeconds(duration * 0.4f);
    }

    private IEnumerator MoveGrabbedObject(bool useLeftHand, float duration)
    {
        Transform handTransform = useLeftHand ? leftHandTransform : rightHandTransform;
        float elapsed = 0f;
        Vector3 startPos = handTransform.position;
        Vector3 targetPos = startPos + new Vector3(
            Random.Range(-0.2f, 0.2f), Random.Range(0.1f, 0.3f), Random.Range(-0.2f, 0.2f));

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            handTransform.position = Vector3.Lerp(startPos, targetPos,
                Mathf.SmoothStep(0, 1, elapsed / duration));
            yield return null;
        }
    }

    private IEnumerator ReleaseObject()
    {
        if (lastGrabActionId > 0)
        {
            interactionRecorder.LogGrabEnd(lastGrabActionId);
            EndRecordingLatency(lastGrabActionId);
            lastGrabActionId = 0;
        }
        yield return new WaitForSeconds(0.5f);
    }

    private void ResetTest()
    {
        StopAllCoroutines();
        vrRig.position = initialPosition;
        vrRig.rotation = initialRotation;
        aoiFilter.ResetFilter();
    }
}
