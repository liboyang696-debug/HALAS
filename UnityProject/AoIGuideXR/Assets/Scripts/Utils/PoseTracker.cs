using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PoseTracker : MonoBehaviour
{
    // Vertical offset to the head position from the tracking origin
    private Vector3 cameraOffset = new Vector3(0, 1.1176f, 0);

    public void GetHeadPose(ref Pose pose)
    {
        pose.position = cameraOffset + gameObject.transform.position;
        pose.rotation = gameObject.transform.rotation;
    }
}
