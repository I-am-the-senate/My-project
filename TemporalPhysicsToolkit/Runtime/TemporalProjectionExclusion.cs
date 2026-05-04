using UnityEngine;

public interface ITemporalProjectionExclusion
{
    bool ExcludeFromTemporalProjection { get; }
}

[DisallowMultipleComponent]
public sealed class TemporalProjectionExclusion : MonoBehaviour, ITemporalProjectionExclusion
{
    [SerializeField] private bool excludeFromTemporalProjection = true;

    public bool ExcludeFromTemporalProjection => excludeFromTemporalProjection;

    public void SetExcluded(bool excluded)
    {
        excludeFromTemporalProjection = excluded;
    }
}
