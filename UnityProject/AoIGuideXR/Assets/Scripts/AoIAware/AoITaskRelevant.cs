using UnityEngine;

/// <summary>
/// Attach to a task object when the task script can explicitly state relevance.
/// Without this component the filter records relevance as Unknown, not False.
/// </summary>
public sealed class AoITaskRelevant : MonoBehaviour
{
    [SerializeField] private bool isRelevant = true;
    public bool IsRelevant { get { return isRelevant; } }
    public void SetRelevant(bool value) { isRelevant = value; }
}
