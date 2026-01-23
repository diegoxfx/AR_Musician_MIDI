using UnityEngine;

public struct DefinedCircle
{
    public Vector3 Center;
    public Vector3 Normal;
    public float Radius;

    public DefinedCircle(Vector3 center, Vector3 normal, float radius)
    {
        Center = center;
        Normal = normal;
        Radius = radius;
    }
}

public interface IPointInputListener
{
    bool IsActive { get; }
    void RegisterPoint(Vector3 worldPosition);
}