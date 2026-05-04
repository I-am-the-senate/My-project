using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class TemporalPhysicsBody : MonoBehaviour
{
    [SerializeField] private string objectId;

    public string ObjectId
    {
        get
        {
            EnsureObjectId();
            return objectId;
        }
    }

    public Rigidbody Rigidbody { get; private set; }

    public static int EnsureAllRigidbodiesHaveTemporalBodies(
        Scene scene,
        bool includeInactive,
        bool includeKinematic = true)
    {
        if (!scene.IsValid())
        {
            scene = SceneManager.GetActiveScene();
        }

        FindObjectsInactive inactiveMode = includeInactive
            ? FindObjectsInactive.Include
            : FindObjectsInactive.Exclude;

        Rigidbody[] rigidbodies = FindObjectsByType<Rigidbody>(inactiveMode);
        int addedCount = 0;
        foreach (Rigidbody rb in rigidbodies)
        {
            if (rb == null || rb.gameObject.scene != scene)
            {
                continue;
            }

            if (!includeKinematic && rb.isKinematic)
            {
                continue;
            }

            if (IsExcludedFromTemporalProjection(rb.gameObject))
            {
                continue;
            }

            TemporalPhysicsBody body = rb.GetComponent<TemporalPhysicsBody>();
            if (body == null)
            {
                body = rb.gameObject.AddComponent<TemporalPhysicsBody>();
                addedCount++;
            }

            body.Rigidbody = rb;
            body.EnsureObjectId();
        }

        EnsureUniqueObjectIds(scene, includeInactive);
        return addedCount;
    }

    public static int EnsureUniqueObjectIds(Scene scene, bool includeInactive)
    {
        if (!scene.IsValid())
        {
            scene = SceneManager.GetActiveScene();
        }

        FindObjectsInactive inactiveMode = includeInactive
            ? FindObjectsInactive.Include
            : FindObjectsInactive.Exclude;

        TemporalPhysicsBody[] bodies = FindObjectsByType<TemporalPhysicsBody>(inactiveMode);
        HashSet<string> usedIds = new HashSet<string>();
        int repairedCount = 0;

        foreach (TemporalPhysicsBody body in bodies)
        {
            if (body == null || body.gameObject.scene != scene)
            {
                continue;
            }

            if (body.IsExcludedFromTemporalProjection())
            {
                continue;
            }

            if (body.Rigidbody == null)
            {
                body.Rigidbody = body.GetComponent<Rigidbody>();
            }

            if (string.IsNullOrWhiteSpace(body.objectId) || usedIds.Contains(body.objectId))
            {
                body.objectId = Guid.NewGuid().ToString("N");
                repairedCount++;
            }

            usedIds.Add(body.objectId);
        }

        return repairedCount;
    }

    private void Awake()
    {
        Rigidbody = GetComponent<Rigidbody>();
        EnsureObjectId();
    }

    private void Reset()
    {
        Rigidbody = GetComponent<Rigidbody>();
        EnsureObjectId();
    }

    private void OnValidate()
    {
        if (string.IsNullOrWhiteSpace(objectId))
        {
            objectId = Guid.NewGuid().ToString("N");
        }
    }

    public TemporalObjectState CaptureState()
    {
        EnsureObjectId();
        if (Rigidbody == null)
        {
            Rigidbody = GetComponent<Rigidbody>();
        }

        TemporalObjectState state = new TemporalObjectState
        {
            objectId = objectId,
            active = gameObject.activeSelf,
            position = Rigidbody != null ? Rigidbody.position : transform.position,
            rotation = Rigidbody != null ? Rigidbody.rotation : transform.rotation,
            scale = transform.localScale,
            linearVelocity = Rigidbody != null ? Rigidbody.linearVelocity : Vector3.zero,
            angularVelocity = Rigidbody != null ? Rigidbody.angularVelocity : Vector3.zero,
            isSleeping = Rigidbody != null && Rigidbody.IsSleeping()
        };

        ITemporalStateSerializable[] logicSources = GetComponents<ITemporalStateSerializable>();
        foreach (ITemporalStateSerializable source in logicSources)
        {
            if (source == null)
            {
                continue;
            }

            string key = source.TemporalStateKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                key = source.GetType().FullName;
            }

            state.logicStates.Add(new TemporalLogicState
            {
                key = key,
                json = source.CaptureTemporalState()
            });
        }

        return state;
    }

    public bool IsExcludedFromTemporalProjection()
    {
        return IsExcludedFromTemporalProjection(gameObject);
    }

    public static bool IsExcludedFromTemporalProjection(GameObject target)
    {
        if (target == null)
        {
            return false;
        }

        MonoBehaviour[] behaviours = target.GetComponentsInParent<MonoBehaviour>(true);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour is ITemporalProjectionExclusion exclusion &&
                exclusion.ExcludeFromTemporalProjection)
            {
                return true;
            }
        }

        return false;
    }

    public void ApplyState(TemporalObjectState state, bool includeVelocities)
    {
        if (state == null)
        {
            return;
        }

        if (!state.active)
        {
            if (Rigidbody == null)
            {
                Rigidbody = GetComponent<Rigidbody>();
            }

            if (Rigidbody != null && !Rigidbody.isKinematic)
            {
                Rigidbody.linearVelocity = Vector3.zero;
                Rigidbody.angularVelocity = Vector3.zero;
                Rigidbody.Sleep();
            }

            gameObject.SetActive(false);
            return;
        }

        if (!gameObject.activeSelf)
        {
            gameObject.SetActive(true);
        }

        if (Rigidbody == null)
        {
            Rigidbody = GetComponent<Rigidbody>();
        }

        if (Rigidbody != null)
        {
            Rigidbody.position = state.position;
            Rigidbody.rotation = state.rotation;
            transform.SetPositionAndRotation(state.position, state.rotation);
            if (includeVelocities && !Rigidbody.isKinematic)
            {
                Rigidbody.linearVelocity = state.linearVelocity;
                Rigidbody.angularVelocity = state.angularVelocity;
                if (state.isSleeping)
                {
                    Rigidbody.Sleep();
                }
                else
                {
                    Rigidbody.WakeUp();
                }
            }
        }
        else
        {
            transform.SetPositionAndRotation(state.position, state.rotation);
        }

        transform.localScale = state.scale;

        ITemporalStateSerializable[] logicTargets = GetComponents<ITemporalStateSerializable>();
        foreach (TemporalLogicState logicState in state.logicStates)
        {
            foreach (ITemporalStateSerializable target in logicTargets)
            {
                if (target == null)
                {
                    continue;
                }

                string key = target.TemporalStateKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = target.GetType().FullName;
                }

                if (key == logicState.key)
                {
                    target.RestoreTemporalState(logicState.json);
                    break;
                }
            }
        }
    }

    private void EnsureObjectId()
    {
        if (!string.IsNullOrWhiteSpace(objectId))
        {
            return;
        }

        objectId = Guid.NewGuid().ToString("N");
    }
}
