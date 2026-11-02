using System;
using UnityEngine;

namespace ExplainableXR
{
    public class TransformTracker : AoIXRTracker
    {
        private Transform targetGobjTransform;

        // Callbacks for continuous movement lifecycle: Begin → Continue → End
        private Func<int> continuousActionBegin;
        private Func<int, int> continuousActionContinue;
        private Func<int, int> continuousActionEnd;

        private float posSensitivity;
        private float rotSensitivity;
        private Vector3 lastPosition;
        private Quaternion lastRotation;
        private bool isMoving = false;
        private int actionId;
        private float lastSignificantChangeTime;
        private readonly float movementEndGraceSeconds;

        public TransformTracker(
            MonoBehaviour monoBehaviour,
            Transform targetGobjTransform,
            Func<int> continuousActionBegin,
            Func<int, int> continuousActionContinue,
            Func<int, int> continuousActionEnd,
            float actionInvokeInterval = 0.5f,
            float posSensitivity = 0.075f,
            float rotSensitivity = 15f,
            float movementEndGraceSeconds = 0.25f)
            : base(monoBehaviour, actionInvokeInterval)
        {
            this.targetGobjTransform = targetGobjTransform;
            this.continuousActionBegin = continuousActionBegin;
            this.continuousActionContinue = continuousActionContinue;
            this.continuousActionEnd = continuousActionEnd;

            this.posSensitivity = posSensitivity;
            this.rotSensitivity = rotSensitivity;
            this.movementEndGraceSeconds = Mathf.Max(actionInvokeInterval,
                movementEndGraceSeconds);
            lastPosition = targetGobjTransform.position;
            lastRotation = targetGobjTransform.rotation;
        }

        protected override void InvokeTrackEvent()
        {
            if (HasTransformChanged())
            {
                lastSignificantChangeTime = Time.unscaledTime;
                if (!isMoving)
                {
                    actionId = continuousActionBegin.Invoke();
                    isMoving = true;
                }
                else
                {
                    int updatedActionId = continuousActionContinue.Invoke(actionId);
                    if (updatedActionId >= 0) actionId = updatedActionId;
                }
            }
            else if (isMoving
                && Time.unscaledTime - lastSignificantChangeTime
                    >= movementEndGraceSeconds)
            {
                actionId = continuousActionEnd.Invoke(actionId);
                isMoving = false;
            }
        }

        private bool HasTransformChanged()
        {
            float dist = Vector3.Distance(targetGobjTransform.position, lastPosition);
            float ang = Quaternion.Angle(targetGobjTransform.rotation, lastRotation);

            if (dist > posSensitivity || ang > rotSensitivity)
            {
                lastPosition = targetGobjTransform.position;
                lastRotation = targetGobjTransform.rotation;
                return true;
            }
            return false;
        }
    }
}
