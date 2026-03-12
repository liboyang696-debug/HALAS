using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public abstract class AoIXRTracker
{
    private MonoBehaviour monobehavior = null;
    private float eventLoopInvokeInterval = 1.0f; // Interval between tracking events (in seconds)
    private Coroutine eventLoopInvoker = null;

    protected AoIXRTracker(MonoBehaviour monobehavior, float eventLoopInvokeInterval)
    {
        this.monobehavior = monobehavior;
        this.eventLoopInvokeInterval = eventLoopInvokeInterval;
    }

    ~AoIXRTracker()
    {
        Stop();
    }

    public void Start()
    {
        if (eventLoopInvoker == null)
            eventLoopInvoker = monobehavior.StartCoroutine(RunEventLoop());
    }

    public void Stop()
    {
        if (eventLoopInvoker != null)
        {
            monobehavior.StopCoroutine(eventLoopInvoker);
            eventLoopInvoker = null;
        }
    }

    private IEnumerator RunEventLoop()
    {
        var waitIntervalCondition = new WaitForSecondsRealtime(eventLoopInvokeInterval);
        while (true)
        {
            yield return waitIntervalCondition;
            InvokeTrackEvent();
        }
    }

    protected abstract void InvokeTrackEvent();
}
