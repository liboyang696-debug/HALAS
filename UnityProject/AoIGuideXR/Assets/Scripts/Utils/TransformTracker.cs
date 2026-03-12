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

        public TransformTracker(
            MonoBehaviour monoBehaviour,
            Transform targetGobjTransform,
            Func<int> continuousActionBegin,
            Func<int, int> continuousActionContinue,
            Func<int, int> continuousActionEnd,
            float actionInvokeInterval = 0.5f,
            float posSensitivity = 0.075f,
            float rotSensitivity = 15f)
            : base(monoBehaviour, actionInvokeInterval)
        {
            this.targetGobjTransform = targetGobjTransform;
            this.continuousActionBegin = continuousActionBegin;
            this.continuousActionContinue = continuousActionContinue;
            this.continuousActionEnd = continuousActionEnd;

            this.posSensitivity = posSensitivity;
            this.rotSensitivity = rotSensitivity;
            lastPosition = targetGobjTransform.position;
            lastRotation = targetGobjTransform.rotation;
        }

        protected override void InvokeTrackEvent()
        {
            if (HasTransformChanged())
            {
                if (!isMoving)
                {
                    actionId = continuousActionBegin.Invoke();
                    isMoving = true;
                }
                else
                    continuousActionContinue.Invoke(actionId);
            }
            else if (isMoving)
            {
                continuousActionEnd.Invoke(actionId);
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
