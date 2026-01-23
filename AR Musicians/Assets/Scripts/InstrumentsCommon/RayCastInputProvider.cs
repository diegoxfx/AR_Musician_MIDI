using Meta.XR;
using Meta.XR.MRUtilityKit;
using UnityEngine;

public class RayCastInputProvider : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField] private Transform controllerAnchor;
    [SerializeField] private MonoBehaviour initialListener;

    private IPointInputListener currentListener;
    private EnvironmentRaycastManager raycastManager;
    private bool useEnvironmentRaycast = true;

    private void Awake()
    {
        if (initialListener is IPointInputListener listener)
        {
            currentListener = listener;
        }

        if (!EnvironmentRaycastManager.IsSupported)
        {
            useEnvironmentRaycast = false;
        }
        else
        {
            raycastManager = FindFirstObjectByType<EnvironmentRaycastManager>();
        }
    }

    // Call this to switch modes (e.g., from defining Piano to Bongo)
    public void SetListener(IPointInputListener newListener)
    {
        currentListener = newListener;
    }

    private void Update()
    {
        // Only run if we have a listener and it wants input
        if (currentListener == null || !currentListener.IsActive) return;

        if (OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) || OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            HandleInput();
        }
    }

    private void HandleInput()
    {
        Vector3 pointToRegister = Vector3.zero;
        bool found = false;

        Ray ray = new Ray(controllerAnchor.position, controllerAnchor.forward);

        // Try Scene Mesh / Depth API
        if (useEnvironmentRaycast && raycastManager != null)
        {
            if (raycastManager.Raycast(ray, out var hit))
            {
                pointToRegister = hit.point;
                found = true;
            }
        }

        // Fallback to Physics
        if (!found && Physics.Raycast(ray, out RaycastHit physicsHit, 100f))
        {
            pointToRegister = physicsHit.point;
            found = true;
        }

        // Fallback to Controller Position (air drawing)
        if (!found)
        {
            pointToRegister = controllerAnchor.position;
        }

        currentListener.RegisterPoint(pointToRegister);
    }
}