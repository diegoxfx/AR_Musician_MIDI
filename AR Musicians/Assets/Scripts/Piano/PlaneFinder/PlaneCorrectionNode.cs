using UnityEngine;

public class PlaneCorrectionNode : MonoBehaviour
{
    public PianoManager manager;
    public int vertexIndex;
    public Vector3 offset;
    public Vector3 lastPosition;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        lastPosition = transform.position;
    }

    // Update is called once per frame
    void Update()
    {
        if (transform.position != lastPosition)
        {
            Vector3 delta = transform.position - lastPosition;
            manager.MovePoint(vertexIndex, delta);
            lastPosition = transform.position;
        }
    }
}
