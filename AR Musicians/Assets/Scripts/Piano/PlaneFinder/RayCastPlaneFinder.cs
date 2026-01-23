using Meta.XR;
using Meta.XR.MRUtilityKit; // Make sure MRUtilityKit is included
using UnityEngine;
using UnityEngine.Serialization;

public class RayCastPlaneFinder : AbstractPlaneFinder
{
    [Header("Ray Casting")]
    public Transform rightControllerAnchor;
    public EnvironmentRaycastManager raycastManager;

    public bool useEnvironmentRaycast = true;

    private void Awake()
    {
        // New in v81+: Check if the Environment Raycast feature is supported at all.
        if (!EnvironmentRaycastManager.IsSupported)
        {
            Debug.LogError("EnvironmentRaycastManager is not supported on this device or in the current configuration. Disabling EnvironmentRaycastManager.");
            useEnvironmentRaycast = false; // Disable functionality
        }
        else
        {
            Debug.Log("EnvironmentRaycastManager is supported and enabled. Using Depth API");
            useEnvironmentRaycast = true;
        }
    }

    private void Update()
    {
        if (active && OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger))
        {
            Debug.Log("Pushed the IndexTrigger");
            var ray = new Ray(
                rightControllerAnchor.position,
                rightControllerAnchor.forward
            );


            Debug.Log("Ray Cast Manager: ", raycastManager);
            Debug.Log("Use Environment Raycast" + useEnvironmentRaycast.ToString());

            if (useEnvironmentRaycast && raycastManager != null && raycastManager.Raycast(ray, out var hit))
            {
                Debug.Log("Hit something");
                CapturePoint(hit.point);
            }
            else if (Physics.Raycast(ray, out RaycastHit physicsHit, 100f))
            {
                CapturePoint(physicsHit.point);
            } else
            {
                Vector3 controllerPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
                CapturePoint(controllerPosition);
            }
        }
    }
}