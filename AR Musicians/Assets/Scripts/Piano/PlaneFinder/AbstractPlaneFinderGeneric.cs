using NUnit.Framework;
using UnityEngine;

public abstract class AbstractPlaneFinderGeneric : MonoBehaviour
{
    #region serialized members
    [Header("Manager")]
    [SerializeField] protected PianoManagerInstrumentDefiner manager;
    #endregion

    #region public members
    public bool active;
    #endregion

    protected void CapturePoint(Vector3 worldPosition)
    {
        manager.RegisterPoint(worldPosition);
    }

    public virtual void Activate()
    {
        active = true;
    }

    public virtual void Deactivate()
    {
        active = false;
    }
}
