using Meta.XR;
using System;
using UnityEngine;

public class CameraViewer : MonoBehaviour
{
    #region Serialized
    // Camera access object which enables fetching the image feed etc.
    [SerializeField] private PassthroughCameraAccess cameraAccess;

    // QuadRenderer which is used to render the image with the found keypoints for debugging.
    [SerializeField] private Renderer quadRenderer;

    // ML Model manager for inference
    [SerializeField] private ModelManager modelManager;
    [SerializeField] private CVPlaneFinder cvPlaneFinder;
    #endregion

    // Texture which stores the camera feed
    private Texture2D picture;

    private void Start()
    {
        // Fixes weird bug that even though it shoudl be contained in the 
        UnityEngine.Android.Permission.RequestUserPermission("horizonos.permission.HEADSET_CAMERA");
    }

    // Update is called once per frame
    void Update()
    {
        if (cameraAccess.IsPlaying)
        {
            if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
            {
                TakePicture();

            }
        }
    }

    void TakePicture()
    {
        Vector2Int resolution = cameraAccess.CurrentResolution;
        int width = resolution[0];
        int height = resolution[1];


        if (picture == null)
        {
            picture = new Texture2D(width, height);
        }

        Color32[] colors = new Color32[width * height];
        colors = cameraAccess.GetColors().ToArray();
        picture.SetPixels32(colors);
        picture.Apply();

        Vector2[] kpts = modelManager.RunInference(cameraAccess.GetTexture(), 4);
        Ray[] rays = new Ray[kpts.Length];

        // Parse the keypoitns to rays
        for (int i = 0; i < kpts.Length; i++)
        {
            Vector2 kpt = kpts[i];
            int y = picture.height - Mathf.FloorToInt(kpt.y);
            var viewportPoint = new Vector2(
                (float)kpt.x / cameraAccess.CurrentResolution.x,
                (float)y / cameraAccess.CurrentResolution.y
            );
            var ray = cameraAccess.ViewportPointToRay(viewportPoint);
            rays[i] = ray;
        }
        // cvPlaneFinder.parseRays(rays);

        //modelManager.RunInferenceAsync(cameraAccess.GetTexture());

        drawQuad(picture, kpts);
        picture.Apply();
        quadRenderer.material.mainTexture = picture;
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
                    picture.SetPixel(x + i, y + j, Color.red);
                }
            }
        }
    }
}
