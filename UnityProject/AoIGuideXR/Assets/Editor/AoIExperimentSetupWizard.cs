using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AoIExperimentSetupWizard
{
    [MenuItem("HALAS/AoI/Install Filter In Current Scene")]
    public static void Install()
    {
        AoIBayesianFilter filter = Object.FindObjectOfType<AoIBayesianFilter>();
        if (filter == null)
        {
            GameObject host = new GameObject("AoI Experiment");
            Undo.RegisterCreatedObjectUndo(host, "Create AoI Experiment");
            filter = Undo.AddComponent<AoIBayesianFilter>(host);
        }

        VRBehaviorRecorder recorder = Object.FindObjectOfType<VRBehaviorRecorder>();
        if (recorder != null)
        {
            SerializedObject serialized = new SerializedObject(recorder);
            SerializedProperty property = serialized.FindProperty("aoiFilter");
            if (property != null)
            {
                property.objectReferenceValue = filter;
                serialized.ApplyModifiedProperties();
            }
        }

        ARBehaviorRecorder arRecorder = Object.FindObjectOfType<ARBehaviorRecorder>();
        if (arRecorder != null)
        {
            SerializedObject serialized = new SerializedObject(arRecorder);
            SerializedProperty property = serialized.FindProperty("aoiFilter");
            if (property != null)
            {
                property.objectReferenceValue = filter;
                serialized.ApplyModifiedProperties();
            }
        }

        Selection.activeObject = filter.gameObject;
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("HALAS action-evidence AoI filter installed. Assign scene references " +
            "and configure task-specific calibrated priors, likelihoods and threshold.");
    }
}
