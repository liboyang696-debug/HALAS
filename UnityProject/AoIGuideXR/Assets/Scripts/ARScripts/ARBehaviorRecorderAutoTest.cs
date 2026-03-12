using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using ExplainableXR;
using Logger = ExplainableXR.Logger;

public class ARBehaviorRecorderAutoTest : MonoBehaviour
{
    [Header("Referenced Components")]
    [SerializeField] private Camera userHeadCam;
    [SerializeField] private ARCameraManager arCameraManager;
    [SerializeField] private AROcclusionManager occlusionManager;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private AoIBayesianFilter aoiFilter;

    [Header("AR Prefabs")]
    [SerializeField] private GameObject arMarkerPrefab;
    [SerializeField] private GameObject unityMarkerPrefab;
    [SerializeField] private GameObject arMemoPrefab;

    [Header("Test Objects (AR Ray Object layer)")]
    [SerializeField] private GameObject[] testObjects;

    [Header("Phase Duration (total 5min)")]
    [SerializeField] private float phase1_RandomMove = 30f;
    [SerializeField] private float phase2_InteractionA = 120f;
    [SerializeField] private float phase3_InteractionB = 120f;
    [SerializeField] private float phase4_RandomMove = 30f;

    [Header("Simulation")]
    [SerializeField] private float moveSpeed = 0.3f;

    private Logger exrLogger;
    private ARMovementRecorder movementRecorder;
    private ARInteractionRecorder interactionRecorder;

    private Vector3 initialPosition;
    private Quaternion initialRotation;
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
        initialPosition = userHeadCam.transform.position;
        initialRotation = userHeadCam.transform.rotation;

        exrLogger = Logger.Initialize(this, userHeadCam, arCameraManager, occlusionManager,
            microphoneManager: null, userID: "ARTestUser");
        UBRParserAndSender.Instance.SetLogger(exrLogger);

        movementRecorder = new ARMovementRecorder(userHeadCam, exrLogger, aoiFilter, 1.0f);
        interactionRecorder = new ARInteractionRecorder(userHeadCam, exrLogger, aoiFilter,
            raycastManager, arMarkerPrefab, unityMarkerPrefab, arMemoPrefab);

        StartCoroutine(RunFullTestSequence());
    }

    private IEnumerator RunFullTestSequence()
    {
        Debug.Log("[Phase 1/4] Random Device Movement 30s");
        yield return StartCoroutine(Phase_RandomMovement(phase1_RandomMove, "Phase1"));

        Debug.Log("[Phase 2/4] Interaction A - Surface Hit + Virtual Object 120s");
        yield return StartCoroutine(Phase_InteractionA(phase2_InteractionA));

        Debug.Log("[Phase 3/4] Interaction B - Floating Memo + Mixed 120s");
        yield return StartCoroutine(Phase_InteractionB(phase3_InteractionB));

        Debug.Log("[Phase 4/4] Random Device Movement 30s");
        yield return StartCoroutine(Phase_RandomMovement(phase4_RandomMove, "Phase4"));

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
            yield return StartCoroutine(SimulateRandomDeviceRotation(0.8f));

            int actionId = movementRecorder.OnDeviceMovementBegin();
            if (actionId > 0)
            {
                StartRecordingLatency($"{phaseName}_Move_{moveCount}",
                    UserActionType.Pose, IntentType.Movement, actionId, false);
                yield return new WaitForSeconds(0.2f);
                movementRecorder.OnDeviceMovementEnd(actionId);
                EndRecordingLatency(actionId);
            }

            yield return StartCoroutine(SimulateRandomDeviceWalk(0.6f));
            yield return new WaitForSeconds(0.2f);
        }

        Debug.Log($"{phaseName} Done - {moveCount} moves, " +
                  $"Movement: {aoiFilter.GetIntentConfidence(IntentType.Movement):F3}");
    }

    // Interaction A: primarily Physical Surface Hit + Virtual Object Inspection
    private IEnumerator Phase_InteractionA(float phaseDuration)
    {
        float phaseStartTime = Time.time;
        int interactionCount = 0;

        while (Time.time - phaseStartTime < phaseDuration)
        {
            if (testObjects != null && testObjects.Length > 0)
            {
                int idx = Random.Range(0, testObjects.Length);
                if (testObjects[idx] != null)
                    yield return StartCoroutine(LookAtTarget(testObjects[idx].transform.position, 0.4f));
            }
            interactionRecorder.OnVirtualObjectUnityRayHit();
            interactionCount++;
            yield return new WaitForSeconds(1.5f);

            yield return StartCoroutine(SimulateRandomDeviceRotation(0.5f));
            interactionRecorder.OnPhysicalSurfaceARRayHit();
            interactionCount++;
            yield return new WaitForSeconds(1.5f);
        }

        Debug.Log($"[Phase2] Interaction A Done - {interactionCount} interactions, " +
                  $"Interaction: {aoiFilter.GetIntentConfidence(IntentType.Interaction):F3}");
    }

    // Interaction B: primarily Floating Memo + mixed all 3 types
    private IEnumerator Phase_InteractionB(float phaseDuration)
    {
        float phaseStartTime = Time.time;
        int interactionCount = 0;

        while (Time.time - phaseStartTime < phaseDuration)
        {
            yield return StartCoroutine(SimulateRandomDeviceRotation(0.3f));
            interactionRecorder.OnFloatingARMemoGenerate();
            interactionCount++;
            yield return new WaitForSeconds(1.2f);

            yield return StartCoroutine(SimulateRandomDeviceRotation(0.3f));
            interactionRecorder.OnPhysicalSurfaceARRayHit();
            interactionCount++;
            yield return new WaitForSeconds(1.0f);

            if (testObjects != null && testObjects.Length > 0)
            {
                int idx = Random.Range(0, testObjects.Length);
                if (testObjects[idx] != null)
                    yield return StartCoroutine(LookAtTarget(testObjects[idx].transform.position, 0.3f));
            }
            interactionRecorder.OnVirtualObjectUnityRayHit();
            interactionCount++;
            yield return new WaitForSeconds(1.0f);

            yield return StartCoroutine(SimulateRandomDeviceRotation(0.3f));
            interactionRecorder.OnFloatingARMemoGenerate();
            interactionCount++;
            yield return new WaitForSeconds(1.0f);
        }

        Debug.Log($"[Phase4] Interaction B Done - {interactionCount} interactions, " +
                  $"Interaction: {aoiFilter.GetIntentConfidence(IntentType.Interaction):F3}");
    }

    private IEnumerator SimulateRandomDeviceRotation(float duration)
    {
        float elapsed = 0f;
        Transform cam = userHeadCam.transform;
        Vector3 startRot = cam.localEulerAngles;
        Vector3 targetRot = startRot + new Vector3(
            Random.Range(-15f, 15f), Random.Range(-30f, 30f), Random.Range(-5f, 5f));

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            cam.localEulerAngles = Vector3.Lerp(startRot, targetRot, elapsed / duration);
            yield return null;
        }
    }

    private IEnumerator SimulateRandomDeviceWalk(float duration)
    {
        float elapsed = 0f;
        Transform cam = userHeadCam.transform;
        Vector3 startPos = cam.position;
        Vector3 targetPos = startPos + new Vector3(
            Random.Range(-0.3f, 0.3f), 0, Random.Range(-0.3f, 0.3f));

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            cam.position = Vector3.Lerp(startPos, targetPos, elapsed / duration);
            yield return null;
        }
    }

    private IEnumerator LookAtTarget(Vector3 targetPosition, float duration)
    {
        Transform cam = userHeadCam.transform;
        Vector3 direction = (targetPosition - cam.position).normalized;
        Quaternion startRot = cam.rotation;
        Quaternion targetRot = Quaternion.LookRotation(direction);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            cam.rotation = Quaternion.Slerp(startRot, targetRot,
                Mathf.SmoothStep(0, 1, elapsed / duration));
            yield return null;
        }
    }

    private void ResetTest()
    {
        StopAllCoroutines();
        userHeadCam.transform.position = initialPosition;
        userHeadCam.transform.rotation = initialRotation;
        aoiFilter.ResetFilter();
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
