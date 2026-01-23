using UnityEngine;

/// <summary>
/// A custom struct to hold all the data for our defined plane.
/// Using a struct is good for small, data-only types.
/// </summary>
public struct DefinedPlane
{
    // The infinite mathematical plane itself.
    public Plane Plane;

    // The four corner points that define the bounded area.
    public Vector3 Corner1;
    public Vector3 Corner2;
    public Vector3 Corner3;
    public Vector3 Corner4;

    // The center point of the four corners, useful for positioning.
    public Vector3 Center { get; }

    public DefinedPlane(Plane plane, Vector3 c1, Vector3 c2, Vector3 c3, Vector3 c4)
    {
        this.Plane = plane;
        this.Corner1 = c1;
        this.Corner2 = c2;
        this.Corner3 = c3;
        this.Corner4 = c4;
        this.Center = (c1 + c2 + c3 + c4) / 4f;
    }
}