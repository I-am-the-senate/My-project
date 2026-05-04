using System;
using System.Collections.Generic;
using UnityEngine;

public enum TemporalPlayerRole
{
    PastHost,
    FutureClient
}

public interface ITemporalStateSerializable
{
    string TemporalStateKey { get; }
    string CaptureTemporalState();
    void RestoreTemporalState(string stateJson);
}

[Serializable]
public class TemporalWorldState
{
    public int tick;
    public float simulatedSeconds;
    public int simulatedSteps;
    public bool converged;
    public List<TemporalObjectState> objects = new List<TemporalObjectState>();

    public string ToJson(bool prettyPrint = false)
    {
        return JsonUtility.ToJson(this, prettyPrint);
    }

    public static TemporalWorldState FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TemporalWorldState();
        }

        return JsonUtility.FromJson<TemporalWorldState>(json);
    }
}

[Serializable]
public class TemporalObjectState
{
    public string objectId;
    public bool active = true;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
    public Vector3 linearVelocity;
    public Vector3 angularVelocity;
    public bool isSleeping;
    public List<TemporalLogicState> logicStates = new List<TemporalLogicState>();
}

[Serializable]
public class TemporalLogicState
{
    public string key;
    public string json;
}
