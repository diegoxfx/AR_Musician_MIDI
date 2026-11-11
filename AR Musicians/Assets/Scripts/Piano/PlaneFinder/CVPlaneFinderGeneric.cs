using Meta.XR;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Android;

public class CVPlaneFinderGeneric : AbstractPlaneFinderGeneric
{
    [Header("Configuration")]
    public EnvironmentRaycastManager raycastManager;


    #region Serialized
    // Camera access object which enables fetching the image feed etc.
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    // QuadRenderer which is used to render the image with the found keypoints for debugging.
    [SerializeField] private Renderer quadRenderer;

    // ML Model manager for inference
    [SerializeField] private ModelManager modelManager;

    // Reuses the instructions in CVmenu so failures are visible inside the headset.
    [SerializeField] private TMP_Text statusText;
    #endregion

    // Texture which stores the camera feed
    private Texture2D picture;
    private bool captureRequirementsMet;

    private const string CaptureInstructions = "Press X on the left controller to detect the keyboard.";
    private const string RetryInstructions = "Press X on the left controller to retry. A on the right confirms only a complete detection.";


    private void Start()
    {
        if (quadRenderer != null) quadRenderer.enabled = active;
    }


    void Update()
    {
        if (!active) return;

        // Update after Android's permission dialog or camera startup without erasing
        // the result of the last detection on every frame.
        bool ready = CheckCaptureRequirements(out string reason);
        if (!ready && captureRequirementsMet && manager != null) manager.ResetDefinition();
        if (!ready || !captureRequirementsMet)
            SetStatus(ready ? CaptureInstructions : reason);
        captureRequirementsMet = ready;

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
        {
            TakePicture();
        }
    }

    private void RequestCapturePermissions()
    {
        var missing = new List<string>();
        if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission))
            missing.Add(OVRPermissionsRequester.PassthroughCameraAccessPermission);
        // EnvironmentRaycastManager waits for this permission; it does not request it.
        if (!Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission))
            missing.Add(OVRPermissionsRequester.ScenePermission);
        if (missing.Count > 0) Permission.RequestUserPermissions(missing.ToArray());
    }

    private bool CheckCaptureRequirements(out string reason)
    {
        bool cameraAllowed = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission);
        bool sceneAllowed = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
        if (!cameraAllowed || !sceneAllowed)
        {
            string missing = !cameraAllowed && !sceneAllowed ? "Camera and spatial data permissions are missing."
                : !cameraAllowed ? "Camera permission is missing." : "Spatial data permission is missing.";
            reason = missing + " Allow access in the headset permission dialog or app settings, then press X (left) to retry. A cannot confirm yet.";
            return false;
        }
        if (manager == null || cameraAccess == null || modelManager == null || raycastManager == null)
        {
            reason = "Detection unavailable: camera, model or spatial raycast setup is missing. A cannot confirm.";
            return false;
        }
        if (!EnvironmentRaycastManager.IsSupported)
        {
            reason = "Spatial raycast is not supported on this device/runtime. Automatic detection cannot define a surface; A cannot confirm.";
            return false;
        }
        if (!cameraAccess.IsPlaying)
        {
            reason = "Waiting for the camera feed. " + RetryInstructions;
            return false;
        }
        reason = null;
        return true;
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
    }

    void TakePicture()
    {
        if (!active || manager == null) return;
        manager.ResetDefinition();
        if (!CheckCaptureRequirements(out string reason))
        {
            SetStatus(reason);
            RequestCapturePermissions();
            return;
        }
        try
        {
            Vector2Int resolution = cameraAccess.CurrentResolution;
            int width = resolution[0];
            int height = resolution[1];

            if (width <= 0 || height <= 0 || cameraAccess.GetTexture() == null)
            {
                SetStatus("Camera image is not ready. " + RetryInstructions);
                return;
            }


            if (picture == null || picture.width != width || picture.height != height)
            {
                if (picture != null) Destroy(picture);
                picture = new Texture2D(width, height);
            }

            Color32[] colors = new Color32[width * height];
            colors = cameraAccess.GetColors().ToArray();
            picture.SetPixels32(colors);
            picture.Apply();

            Vector2[] kpts = modelManager.RunInference(cameraAccess.GetTexture(), 4);
            if (kpts == null || kpts.Length != 4)
            {
                SetStatus("Incomplete keyboard detection: three corner rays are required. " + RetryInstructions);
                return;
            }
            Ray[] rays = new Ray[kpts.Length - 1];

            // Parse the keypoitns to rays
            for (int i = 0; i < kpts.Length - 1; i++)
            {
                Vector2 kpt = kpts[i];
                if (float.IsNaN(kpt.x) || float.IsInfinity(kpt.x) || float.IsNaN(kpt.y) || float.IsInfinity(kpt.y)
                    || kpt.x < 0 || kpt.x >= width || kpt.y < 0 || kpt.y >= height)
                {
                    SetStatus("Incomplete keyboard detection: a corner is outside the camera image. " + RetryInstructions);
                    return;
                }
                int y = picture.height - Mathf.FloorToInt(kpt.y);
                var viewportPoint = new Vector2(
                    (float)kpt.x / cameraAccess.CurrentResolution.x,
                    (float)y / cameraAccess.CurrentResolution.y
                );
                var ray = cameraAccess.ViewportPointToRay(viewportPoint);
                rays[i] = ray;
            }

            parseRays(rays);

            drawQuad(picture, kpts);
            picture.Apply();
            if (quadRenderer != null) quadRenderer.material.mainTexture = picture;
        }
        catch (Exception exception)
        {
            manager.ResetDefinition();
            Debug.LogWarning("Keyboard detection failed: " + exception.Message);
            SetStatus("Keyboard detection failed; no surface can be confirmed. " + RetryInstructions);
        }
    }

    /// <summary>
    /// Draws keypoitns on top of the picture.
    /// </summary>
    /// <param name="picture">The picture texture.</param>
    /// <param name="kpts">Array of keypoints in pixel space.</param>
    void drawQuad(Texture2D picture, Vector2[] kpts)
    {
        int width = 3; // width of the quad in image space.
        foreach (Vector2 kpt in kpts)
        {
            int x = Mathf.FloorToInt(kpt.x);
            int y = picture.height - Mathf.FloorToInt(kpt.y);
            for (int i = -width; i < width; i++)
            {
                for (int j = -width; j < width; j++)
                {
                    if (x + i >= 0 && x + i < picture.width && y + j >= 0 && y + j < picture.height)
                        picture.SetPixel(x + i, y + j, Color.red);
                }
            }
        }
    }

    public void parseRays(Ray[] rays)
    {
        if (!active || manager == null) return;
        manager.ResetDefinition();
        if (!CheckCaptureRequirements(out string reason))
        {
            SetStatus(reason);
            return;
        }
        if (rays == null || rays.Length != 3)
        {
            SetStatus("Incomplete keyboard detection: three corner rays are required. " + RetryInstructions);
            return;
        }

        // Commit one complete attempt only: failed rays must never leave partial
        // points that a later capture or manual controller input can complete.
        var points = new Vector3[3];
        for (int i = 0; i < rays.Length; i++)
        {
            if (!raycastManager.Raycast(rays[i], out var hit))
            {
                SetStatus(hit.status == EnvironmentRaycastHitStatus.NotReady
                    ? "Spatial raycast is not ready. Wait for spatial data, then press X (left) to retry. A cannot confirm yet."
                    : "Incomplete keyboard detection: a corner did not hit a visible surface. " + RetryInstructions);
                return;
            }
            points[i] = hit.point;
        }
        bool accepted = manager.TryRegisterDetectedPoints(points);
        SetStatus(accepted && manager.CanConfirm
            ? "Keyboard surface detected. Adjust the corners if needed. Press A on the right controller to confirm, or X on the left to retry."
            : "Incomplete keyboard detection: the three surface corners are invalid. " + RetryInstructions);
    }

    override public void Activate()
    {
        Debug.Log("CVPlaneFinderGeneric: Activated");
        base.Activate();
        if (quadRenderer != null) quadRenderer.enabled = true;
        RequestCapturePermissions();
        captureRequirementsMet = CheckCaptureRequirements(out string reason);
        SetStatus(captureRequirementsMet ? CaptureInstructions : reason);
    }

    public override void Deactivate()
    {
        base.Deactivate();
        if (quadRenderer != null) quadRenderer.enabled = false;
    }
    
}
