using UnityEngine;
using Meta.XR;

public class CVCircleFinder : MonoBehaviour
{
    [Header("Manager")]
    [SerializeField] private BongosManager manager;
    public bool active;

    [Header("Configuration")]
    public EnvironmentRaycastManager raycastManager;
    [SerializeField] private PassthroughCameraAccess cameraAccess;
    [SerializeField] private Renderer quadRenderer;
    [SerializeField] private ModelManager modelManager;
    private Texture2D picture;

    void Start()
    {
        UnityEngine.Android.Permission.RequestUserPermission("horizonos.permission.HEADSET_CAMERA");
        quadRenderer.enabled = false;
    }

    // Update is called once per frame
    void Update()
    {
        if (cameraAccess.IsPlaying)
        {
            if (active && OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch))
            {
                manager.ResetDefinition();
                TakePicture();
            }
        }
    }

    void TakePicture()
    {
        manager.ResetDefinition();
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

        Vector2[] kpts = modelManager.RunInference(cameraAccess.GetTexture(), 6);
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

        parseRays(rays);

        drawQuad(picture, kpts);
        picture.Apply();
        quadRenderer.material.mainTexture = picture;
    }


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

    public void parseRays(Ray[] rays)
    {
        foreach (Ray ray in rays)
        {
            Debug.Log("Shooting ray: " + ray);
            if (raycastManager.Raycast(ray, out var hit))
            {
                manager.RegisterPoint(hit.point);
            }
            else
            {
                Debug.Log("No hitpoint found...");
            }
        }
    }

    public void Activate()
    {
        active = true;
        quadRenderer.enabled = true;
    }

    public void Deactivate()
    {
        Debug.Log("Deactiving the cv circle finder...");
        quadRenderer.enabled = false;
        active = false;
    }
}
