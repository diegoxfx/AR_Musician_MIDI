using Meta.XR;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Android;

public class CVPlaneFinderGeneric : AbstractPlaneFinderGeneric
{
    private enum CaptureStatus
    {
        Ready,
        MissingPermissions,
        MissingCameraPermission,
        MissingSpatialPermission,
        MissingSetup,
        SpatialUnsupported,
        WaitingForCamera,
        CameraImageUnavailable,
        RejectedImage,
        CaptureFailed,
        MissingRays,
        SpatialNotReady,
        SpatialMiss,
        InvalidSurface,
        SurfaceDetected
    }

    [Header("Configuration")]
    public EnvironmentRaycastManager raycastManager;


    #region Serialized
    // Camera access object which enables fetching the image feed etc.
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    // QuadRenderer which is used to render the image with the found keypoints for debugging.
    [SerializeField] private Renderer quadRenderer;

    // ML Model manager for inference
    [SerializeField] private ModelManager modelManager;

    [SerializeField, Range(0f, 1f)] private float minimumCornerConfidence = PianoDetectionValidator.DefaultMinimumConfidence;

    // Reuses the instructions in CVmenu so failures are visible inside the headset.
    [SerializeField] private TMP_Text statusText;
    #endregion

    // Texture which stores the camera feed
    private Texture2D picture;
    private bool captureRequirementsMet;
    private CaptureStatus captureStatus;
    private PianoDetectionIssue detectionIssue;

    public string StatusMessage => DescribeStatus();

    private static string CaptureInstructions => AppText.Choose(
        "Press X on the left controller to detect the keyboard.",
        "Pulsa X en el mando izquierdo para detectar el teclado.");
    private static string RetryInstructions => AppText.Choose(
        "Press X on the left controller to retry. A on the right confirms only a complete detection.",
        "Pulsa X en el mando izquierdo para reintentar. A en el derecho confirma una detección completa.");
    private static string ReframeInstructions => AppText.Choose(
        "Move back and center all the keys, then press X (left) to retry. You can also use manual setup.",
        "Aléjate y centra todas las teclas. Pulsa X (izquierdo) para reintentar o usa la configuración manual.");


    private void Start()
    {
        if (quadRenderer != null) quadRenderer.enabled = active;
    }

    private void OnEnable() => AppText.Changed += RefreshStatusText;
    private void OnDisable() => AppText.Changed -= RefreshStatusText;


    void Update()
    {
        if (!active) return;

        // Update after Android's permission dialog or camera startup without erasing
        // the result of the last detection on every frame.
        bool ready = CheckCaptureRequirements(out CaptureStatus reason);
        if (!ready && captureRequirementsMet && manager != null) manager.ResetDefinition();
        if (!ready || !captureRequirementsMet)
            SetStatus(reason);
        captureRequirementsMet = ready;

        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
        {
            CaptureKeyboard();
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

    private bool CheckCaptureRequirements(out CaptureStatus reason)
    {
        bool cameraAllowed = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.PassthroughCameraAccessPermission);
        bool sceneAllowed = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
        if (!cameraAllowed || !sceneAllowed)
        {
            reason = !cameraAllowed && !sceneAllowed ? CaptureStatus.MissingPermissions
                : !cameraAllowed ? CaptureStatus.MissingCameraPermission : CaptureStatus.MissingSpatialPermission;
            return false;
        }
        if (manager == null || cameraAccess == null || modelManager == null || raycastManager == null)
        {
            reason = CaptureStatus.MissingSetup;
            return false;
        }
        if (!EnvironmentRaycastManager.IsSupported)
        {
            reason = CaptureStatus.SpatialUnsupported;
            return false;
        }
        if (!cameraAccess.IsPlaying)
        {
            reason = CaptureStatus.WaitingForCamera;
            return false;
        }
        reason = CaptureStatus.Ready;
        return true;
    }

    private void SetStatus(CaptureStatus status)
    {
        captureStatus = status;
        RefreshStatusText();
    }

    private void RefreshStatusText()
    {
        if (statusText != null) statusText.text = StatusMessage;
    }

    public void CaptureKeyboard()
    {
        if (!active || manager == null) return;
        manager.ResetDefinition();
        captureRequirementsMet = CheckCaptureRequirements(out CaptureStatus reason);
        if (!captureRequirementsMet)
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
            Texture cameraTexture = cameraAccess.GetTexture();

            if (width <= 0 || height <= 0 || cameraTexture == null)
            {
                SetStatus(CaptureStatus.CameraImageUnavailable);
                return;
            }


            if (picture == null || picture.width != width || picture.height != height)
            {
                if (picture != null) Destroy(picture);
                picture = new Texture2D(width, height);
            }

            picture.SetPixels32(cameraAccess.GetColors().ToArray());
            picture.Apply();
            if (quadRenderer != null) quadRenderer.material.mainTexture = picture;

            Vector2[] kpts = modelManager.RunInference(cameraTexture, 4, out float[] confidences);
            detectionIssue = PianoDetectionValidator.Validate(kpts, confidences, width, height, minimumCornerConfidence);
            if (detectionIssue != PianoDetectionIssue.None)
            {
                SetStatus(CaptureStatus.RejectedImage);
                return;
            }

            // All four corners validate the image. The plane uses TL, TR and BR.
            Ray[] rays = new Ray[3];
            for (int i = 0; i < rays.Length; i++)
            {
                Vector2 kpt = kpts[i];
                int y = picture.height - Mathf.FloorToInt(kpt.y);
                var viewportPoint = new Vector2(kpt.x / width, (float)y / height);
                rays[i] = cameraAccess.ViewportPointToRay(viewportPoint);
            }

            parseRays(rays);

            drawQuad(picture, kpts);
            picture.Apply();
        }
        catch (Exception exception)
        {
            manager.ResetDefinition();
            Debug.LogWarning("Keyboard detection failed: " + exception.Message);
            SetStatus(CaptureStatus.CaptureFailed);
        }
    }

    private string DescribeStatus()
    {
        switch (captureStatus)
        {
            case CaptureStatus.MissingPermissions:
                return DescribeMissingPermission("Camera and spatial data permissions are missing.", "Faltan los permisos de cámara y datos espaciales.");
            case CaptureStatus.MissingCameraPermission:
                return DescribeMissingPermission("Camera permission is missing.", "Falta el permiso de cámara.");
            case CaptureStatus.MissingSpatialPermission:
                return DescribeMissingPermission("Spatial data permission is missing.", "Falta el permiso de datos espaciales.");
            case CaptureStatus.MissingSetup:
                return AppText.Choose("Detection unavailable. Camera, model or spatial raycast setup is missing. Use manual setup.",
                    "Detección no disponible. Falta la cámara, el modelo o la proyección espacial. Usa la configuración manual.");
            case CaptureStatus.SpatialUnsupported:
                return AppText.Choose("Spatial raycast is not supported on this device. Use manual setup.",
                    "Este dispositivo no admite la proyección espacial. Usa la configuración manual.");
            case CaptureStatus.WaitingForCamera:
                return AppText.Choose("Waiting for the camera feed.", "Esperando la imagen de la cámara.") + " " + RetryInstructions;
            case CaptureStatus.CameraImageUnavailable:
                return AppText.Choose("Camera image is not ready.", "La imagen de la cámara aún no está disponible.") + " " + RetryInstructions;
            case CaptureStatus.RejectedImage:
                return DescribeDetectionIssue(detectionIssue);
            case CaptureStatus.CaptureFailed:
                return AppText.Choose("Keyboard detection failed.", "No se pudo detectar el teclado.") + " " + RetryInstructions;
            case CaptureStatus.MissingRays:
                return AppText.Choose("Incomplete keyboard detection. Unable to project three corner rays.",
                    "Detección incompleta. No se pudieron proyectar tres esquinas.") + " " + RetryInstructions;
            case CaptureStatus.SpatialNotReady:
                return AppText.Choose("Spatial raycast is not ready. Wait for spatial data, then press X (left) to retry. A cannot confirm yet.",
                    "Los datos espaciales aún no están disponibles. Espera y pulsa X (izquierdo) para reintentar.");
            case CaptureStatus.SpatialMiss:
                return AppText.Choose("Incomplete keyboard detection. A corner did not hit a visible surface.",
                    "Detección incompleta. Una esquina no coincide con una superficie visible.") + " " + RetryInstructions;
            case CaptureStatus.InvalidSurface:
                return AppText.Choose("Incomplete keyboard detection. The three surface corners are invalid.",
                    "Detección incompleta. Las tres esquinas proyectadas no forman una superficie válida.") + " " + RetryInstructions;
            case CaptureStatus.SurfaceDetected:
                return AppText.Choose("Keyboard surface detected. Adjust the corners if needed. Press A on the right controller to confirm, or X on the left to retry.",
                    "Superficie del teclado detectada. Ajusta las esquinas si hace falta. Pulsa A (derecho) para confirmar o X (izquierdo) para reintentar.");
            default:
                return CaptureInstructions;
        }
    }

    private static string DescribeMissingPermission(string english, string spanish)
    {
        return AppText.Choose(english, spanish) + " " + AppText.Choose(
            "Allow access in the headset permission dialog or app settings, then press X (left) to retry. A cannot confirm yet.",
            "Concede el permiso en el visor o en los ajustes de la app. Luego pulsa X (izquierdo) para reintentar.");
    }

    private static string DescribeDetectionIssue(PianoDetectionIssue issue)
    {
        string description;
        switch (issue)
        {
            case PianoDetectionIssue.InvalidCorner:
                description = AppText.Choose("A detected corner is outside the camera image.", "Una esquina detectada queda fuera de la imagen.");
                break;
            case PianoDetectionIssue.ClippedCorner:
                description = AppText.Choose("A detected corner reaches the edge of the camera image.", "Una esquina detectada toca el borde de la imagen.");
                break;
            case PianoDetectionIssue.LowConfidence:
                description = AppText.Choose("A keyboard corner could not be located reliably.", "Una esquina del teclado no se pudo localizar con suficiente confianza.");
                break;
            case PianoDetectionIssue.InvalidGeometry:
                description = AppText.Choose("The detected corners do not form a usable keyboard outline.", "Las esquinas detectadas no forman un contorno válido del teclado.");
                break;
            default:
                description = AppText.Choose("Incomplete keyboard detection. All four corners must be visible.",
                    "Detección incompleta. Se necesitan las cuatro esquinas visibles para ubicar el teclado.");
                break;
        }
        return description + " " + ReframeInstructions;
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
        if (!CheckCaptureRequirements(out CaptureStatus reason))
        {
            SetStatus(reason);
            return;
        }
        if (rays == null || rays.Length != 3)
        {
            SetStatus(CaptureStatus.MissingRays);
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
                    ? CaptureStatus.SpatialNotReady : CaptureStatus.SpatialMiss);
                return;
            }
            points[i] = hit.point;
        }
        bool accepted = manager.TryRegisterDetectedPoints(points);
        SetStatus(accepted && manager.CanConfirm ? CaptureStatus.SurfaceDetected : CaptureStatus.InvalidSurface);
    }

    override public void Activate()
    {
        Debug.Log("CVPlaneFinderGeneric: Activated");
        base.Activate();
        if (quadRenderer != null) quadRenderer.enabled = true;
        RequestCapturePermissions();
        captureRequirementsMet = CheckCaptureRequirements(out CaptureStatus reason);
        SetStatus(reason);
    }

    public override void Deactivate()
    {
        base.Deactivate();
        if (quadRenderer != null) quadRenderer.enabled = false;
    }
    
}
