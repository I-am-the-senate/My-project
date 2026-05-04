using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class PastFutureTimelineController : MonoBehaviour
{
    [Serializable]
    public sealed class FutureStateReadyEvent : UnityEvent<string>
    {
    }

    private struct BodySample
    {
        public Vector3 position;
    }

    [Header("Timeline")]
    [SerializeField] private TemporalPlayerRole role = TemporalPlayerRole.PastHost;
    [SerializeField] private MonoBehaviour temporalPhysicsProjector;
    [SerializeField] private bool autoFindTrackedBodies = true;
    [SerializeField] private List<TemporalPhysicsBody> trackedBodies = new List<TemporalPhysicsBody>();

    [Header("Startup")]
    [SerializeField] private bool projectOnceOnStart = true;
    [SerializeField] private float initialProjectionDelay;

    [Header("Disturbance")]
    [SerializeField] private bool autoProjectOnAnyBodyDisturbance;
    [SerializeField] private float linearVelocityThreshold = 0.1f;
    [SerializeField] private float angularVelocityThreshold = 0.1f;
    [SerializeField] private float displacementThreshold = 0.025f;
    [SerializeField] private float projectionRequestCooldown = 0.25f;
    [SerializeField] private int requiredQuietFramesBeforeAutoProjection = 8;
    [SerializeField] private bool suppressRequestsWhileProjectionPending = true;

    [Header("Future Apply")]
    [SerializeField] private bool includeVelocitiesWhenApplyingFutureState = true;

    [Header("Debug")]
    [SerializeField] private bool showDebugGui = true;
    [SerializeField] private Vector2 debugGuiPosition = new Vector2(12f, 12f);
    [SerializeField] private Vector2 debugGuiSize = new Vector2(360f, 220f);

    [Header("Events")]
    [SerializeField] private FutureStateReadyEvent onFutureStateReady = new FutureStateReadyEvent();

    public event Action<string> OnFutureStateReady;

    private readonly Dictionary<TemporalPhysicsBody, BodySample> bodySamples = new Dictionary<TemporalPhysicsBody, BodySample>();

    private string latestFutureStateJson;
    private MonoBehaviour subscribedProjector;
    private EventInfo subscribedProjectionCompletedEvent;
    private Delegate subscribedProjectionCompletedDelegate;
    private float lastProjectionRequestTime = -9999f;
    private float projectionPendingSince = -1f;
    private int projectionRequestCount;
    private int projectionCompletionCount;
    private int receivedFutureStateCount;
    private int appliedFutureStateCount;
    private string lastStatus = "Idle";
    private string lastProjectionReason = "None";
    private bool projectionPending;
    private bool pastWorldDirty;
    private int pastWorldQuietFrames;
    private int lastTrackedRefreshFrame = -1;
    private string pendingAutoProjectionReason = "Past world changed";
    private bool initialProjectionRequested;

    public TemporalPlayerRole Role
    {
        get => role;
        set => role = value;
    }

    public string LatestFutureStateJson => latestFutureStateJson;

    public IReadOnlyList<TemporalPhysicsBody> TrackedBodies => trackedBodies;

    public FutureStateReadyEvent FutureStateReadyUnityEvent => onFutureStateReady;

    private void Awake()
    {
        RefreshTrackedBodiesIfNeeded();
        ResampleTrackedBodies();
    }

    private void OnEnable()
    {
        RefreshTrackedBodiesIfNeeded();
        ResampleTrackedBodies();
        TrySubscribeToProjector();
    }

    private IEnumerator Start()
    {
        if (!projectOnceOnStart || role != TemporalPlayerRole.PastHost)
        {
            yield break;
        }

        if (initialProjectionDelay > 0f)
        {
            yield return new WaitForSeconds(initialProjectionDelay);
        }
        else
        {
            yield return null;
        }

        if (!isActiveAndEnabled || initialProjectionRequested)
        {
            yield break;
        }

        initialProjectionRequested = true;
        RefreshTrackedBodies();
        ResampleTrackedBodies();
        RequestProjection("Initial scene projection", true);
    }

    private void OnDisable()
    {
        UnsubscribeFromProjector();
    }

    private void FixedUpdate()
    {
        RefreshTrackedBodiesIfNeeded();
        TrySubscribeToProjector();

        if (role == TemporalPlayerRole.PastHost && autoProjectOnAnyBodyDisturbance)
        {
            MonitorPastHostDisturbances();
        }
    }

    private void OnValidate()
    {
        linearVelocityThreshold = Mathf.Max(0f, linearVelocityThreshold);
        angularVelocityThreshold = Mathf.Max(0f, angularVelocityThreshold);
        displacementThreshold = Mathf.Max(0f, displacementThreshold);
        projectionRequestCooldown = Mathf.Max(0f, projectionRequestCooldown);
        initialProjectionDelay = Mathf.Max(0f, initialProjectionDelay);
        requiredQuietFramesBeforeAutoProjection = Mathf.Max(1, requiredQuietFramesBeforeAutoProjection);
        debugGuiSize.x = Mathf.Max(240f, debugGuiSize.x);
        debugGuiSize.y = Mathf.Max(140f, debugGuiSize.y);
    }

    public void RefreshTrackedBodies()
    {
        int previousCount = trackedBodies.Count;
        TemporalPhysicsBody.EnsureAllRigidbodiesHaveTemporalBodies(gameObject.scene, false);

        trackedBodies.Clear();
        TemporalPhysicsBody[] bodies = FindObjectsByType<TemporalPhysicsBody>(FindObjectsInactive.Exclude);
        foreach (TemporalPhysicsBody body in bodies)
        {
            if (body != null &&
                body.gameObject.scene == gameObject.scene &&
                !body.IsExcludedFromTemporalProjection() &&
                !trackedBodies.Contains(body))
            {
                trackedBodies.Add(body);
            }
        }

        PruneBodySamples();
        if (trackedBodies.Count != previousCount)
        {
            lastStatus = $"Tracking {trackedBodies.Count} temporal bodies";
        }
    }

    public void RegisterTrackedBody(TemporalPhysicsBody body)
    {
        if (body == null || trackedBodies.Contains(body))
        {
            return;
        }

        if (body.IsExcludedFromTemporalProjection())
        {
            return;
        }

        trackedBodies.Add(body);
        SampleBody(body);
    }

    public void UnregisterTrackedBody(TemporalPhysicsBody body)
    {
        if (body == null)
        {
            return;
        }

        trackedBodies.Remove(body);
        bodySamples.Remove(body);
    }

    public bool RequestProjection()
    {
        return RequestProjection("Manual request", true);
    }

    public bool RequestProjection(string reason)
    {
        return RequestProjection(reason, true);
    }

    public void NotifyPastInfluenceStarted(string reason)
    {
        if (role != TemporalPlayerRole.PastHost)
        {
            return;
        }

        lastProjectionReason = string.IsNullOrWhiteSpace(reason)
            ? "Past influence started"
            : reason;
        lastStatus = "Past influence active: " + lastProjectionReason;
    }

    public bool NotifyPastInfluenceEnded(string reason)
    {
        if (role != TemporalPlayerRole.PastHost)
        {
            return false;
        }

        string projectionReason = string.IsNullOrWhiteSpace(reason)
            ? "Past influence ended"
            : reason;

        return RequestProjection(projectionReason, true);
    }

    public bool NotifyPastObjectDropped(TemporalPhysicsBody body)
    {
        string bodyName = body != null ? body.name : "object";
        return NotifyPastInfluenceEnded("Dropped " + bodyName);
    }

    public void ReceiveFutureStateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            lastStatus = "Ignored empty future state JSON";
            return;
        }

        CacheFutureStateJson(json, false);
        receivedFutureStateCount++;

        TemporalWorldState futureState;
        try
        {
            futureState = TemporalWorldState.FromJson(json);
        }
        catch (Exception exception)
        {
            lastStatus = $"Failed to parse future state JSON: {exception.Message}";
            return;
        }

        if (futureState == null)
        {
            lastStatus = "Parsed future state was null";
            return;
        }

        bool applied = TryApplyFutureStateWithProjector(futureState);
        if (!applied)
        {
            int directApplyCount = ApplyFutureStateDirectly(futureState);
            applied = directApplyCount > 0;
            lastStatus = applied
                ? $"Applied future state directly to {directApplyCount} tracked bodies"
                : "No projector apply method or matching tracked bodies found";
        }

        if (applied)
        {
            appliedFutureStateCount++;
        }
    }

    private void RefreshTrackedBodiesIfNeeded()
    {
        if (!autoFindTrackedBodies || lastTrackedRefreshFrame == Time.frameCount)
        {
            return;
        }

        lastTrackedRefreshFrame = Time.frameCount;
        RefreshTrackedBodies();
    }

    private void MonitorPastHostDisturbances()
    {
        if (trackedBodies.Count == 0)
        {
            return;
        }

        bool disturbed = TryFindDisturbance(out string reason);
        bool quiet = AreTrackedBodiesQuiet();

        if (disturbed)
        {
            pastWorldDirty = true;
            pastWorldQuietFrames = 0;
            pendingAutoProjectionReason = reason;
            lastProjectionReason = reason;
            lastStatus = "Past world changed: " + reason;

            if (CanRequestProjection() && RequestProjection(reason, false))
            {
                pastWorldDirty = false;
                return;
            }
        }

        if (pastWorldDirty && quiet)
        {
            pastWorldQuietFrames++;
            if (pastWorldQuietFrames >= Mathf.Max(1, requiredQuietFramesBeforeAutoProjection) &&
                CanRequestProjection())
            {
                string projectionReason = "Past world settled after " + pendingAutoProjectionReason;
                if (RequestProjection(projectionReason, false))
                {
                    pastWorldDirty = false;
                    pastWorldQuietFrames = 0;
                }
            }
        }
        else if (!quiet)
        {
            pastWorldQuietFrames = 0;
        }

        ResampleTrackedBodies();
    }

    private bool TryFindDisturbance(out string reason)
    {
        PruneBodySamples();

        foreach (TemporalPhysicsBody body in trackedBodies)
        {
            if (body == null)
            {
                continue;
            }

            if (!bodySamples.TryGetValue(body, out BodySample sample))
            {
                SampleBody(body);
                continue;
            }

            Rigidbody bodyRigidbody = body.Rigidbody != null ? body.Rigidbody : body.GetComponent<Rigidbody>();
            Vector3 position = bodyRigidbody != null ? bodyRigidbody.position : body.transform.position;
            float displacement = Vector3.Distance(sample.position, position);

            if (displacementThreshold > 0f && displacement > displacementThreshold)
            {
                reason = $"{body.name} displacement {displacement:0.000}";
                return true;
            }

            if (bodyRigidbody == null)
            {
                continue;
            }

            float linearSpeed = bodyRigidbody.linearVelocity.magnitude;
            if (linearVelocityThreshold > 0f && linearSpeed > linearVelocityThreshold)
            {
                reason = $"{body.name} linear speed {linearSpeed:0.000}";
                return true;
            }

            float angularSpeed = bodyRigidbody.angularVelocity.magnitude;
            if (angularVelocityThreshold > 0f && angularSpeed > angularVelocityThreshold)
            {
                reason = $"{body.name} angular speed {angularSpeed:0.000}";
                return true;
            }
        }

        reason = null;
        return false;
    }

    private bool AreTrackedBodiesQuiet()
    {
        PruneBodySamples();

        bool hasBody = false;
        foreach (TemporalPhysicsBody body in trackedBodies)
        {
            if (body == null || !body.gameObject.activeInHierarchy)
            {
                continue;
            }

            hasBody = true;
            Rigidbody bodyRigidbody = body.Rigidbody != null ? body.Rigidbody : body.GetComponent<Rigidbody>();
            Vector3 position = bodyRigidbody != null ? bodyRigidbody.position : body.transform.position;

            if (bodySamples.TryGetValue(body, out BodySample sample) &&
                displacementThreshold > 0f &&
                Vector3.Distance(sample.position, position) > displacementThreshold)
            {
                return false;
            }

            if (bodyRigidbody == null || bodyRigidbody.isKinematic)
            {
                continue;
            }

            if (linearVelocityThreshold > 0f &&
                bodyRigidbody.linearVelocity.magnitude > linearVelocityThreshold)
            {
                return false;
            }

            if (angularVelocityThreshold > 0f &&
                bodyRigidbody.angularVelocity.magnitude > angularVelocityThreshold)
            {
                return false;
            }
        }

        return hasBody;
    }

    private bool CanRequestProjection()
    {
        if (Time.time - lastProjectionRequestTime < projectionRequestCooldown)
        {
            return false;
        }

        if (suppressRequestsWhileProjectionPending && projectionPending)
        {
            return false;
        }

        return true;
    }

    private bool RequestProjection(string reason, bool force)
    {
        if (!force && !CanRequestProjection())
        {
            return false;
        }

        EnsureProjectorReference();
        if (temporalPhysicsProjector == null)
        {
            lastStatus = "No TemporalPhysicsProjector reference found";
            return false;
        }

        MethodInfo requestMethod = FindProjectionRequestMethod(temporalPhysicsProjector.GetType(), out object[] requestParameters);

        if (requestMethod == null)
        {
            lastStatus = "Projector does not expose RequestProjection(), ProjectCurrentScene(), or StartProjection()";
            return false;
        }

        try
        {
            requestMethod.Invoke(temporalPhysicsProjector, requestParameters);
        }
        catch (Exception exception)
        {
            lastStatus = $"{requestMethod.Name} failed: {UnwrapReflectionException(exception).Message}";
            return false;
        }

        projectionRequestCount++;
        projectionPending = true;
        projectionPendingSince = Time.time;
        lastProjectionRequestTime = Time.time;
        lastProjectionReason = reason;
        lastStatus = $"Projection requested via {requestMethod.Name}: {reason}";
        ResampleTrackedBodies();
        return true;
    }

    private MethodInfo FindProjectionRequestMethod(Type projectorType, out object[] parameters)
    {
        parameters = null;
        if (projectorType == null)
        {
            return null;
        }

        string[] methodNames = { "RequestProjection", "ProjectCurrentScene", "StartProjection" };
        foreach (string methodName in methodNames)
        {
            MethodInfo noArgumentMethod = projectorType.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);

            if (noArgumentMethod != null)
            {
                return noArgumentMethod;
            }

            MethodInfo callbackMethod = projectorType.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Action<TemporalWorldState>) },
                null);

            if (callbackMethod != null)
            {
                parameters = new object[] { new Action<TemporalWorldState>(HandleProjectionCompletedState) };
                return callbackMethod;
            }
        }

        return null;
    }

    private bool TryApplyFutureStateWithProjector(TemporalWorldState futureState)
    {
        EnsureProjectorReference();
        if (temporalPhysicsProjector == null)
        {
            return false;
        }

        Type projectorType = temporalPhysicsProjector.GetType();
        MethodInfo applyMethod = projectorType.GetMethod(
            "ApplyFutureStateToLiveBodies",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(TemporalWorldState), typeof(bool) },
            null);

        object[] parameters;
        if (applyMethod != null)
        {
            parameters = new object[] { futureState, includeVelocitiesWhenApplyingFutureState };
        }
        else
        {
            applyMethod = projectorType.GetMethod(
                "ApplyFutureStateToLiveBodies",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(TemporalWorldState) },
                null);
            parameters = new object[] { futureState };
        }

        if (applyMethod == null)
        {
            return false;
        }

        try
        {
            applyMethod.Invoke(temporalPhysicsProjector, parameters);
        }
        catch (Exception exception)
        {
            lastStatus = $"ApplyFutureStateToLiveBodies failed: {UnwrapReflectionException(exception).Message}";
            return false;
        }

        lastStatus = "Applied future state through projector";
        return true;
    }

    private int ApplyFutureStateDirectly(TemporalWorldState futureState)
    {
        if (futureState == null || futureState.objects == null)
        {
            return 0;
        }

        RefreshTrackedBodiesIfNeeded();

        Dictionary<string, TemporalObjectState> statesById = new Dictionary<string, TemporalObjectState>();
        foreach (TemporalObjectState objectState in futureState.objects)
        {
            if (objectState != null && !string.IsNullOrWhiteSpace(objectState.objectId))
            {
                statesById[objectState.objectId] = objectState;
            }
        }

        int appliedCount = 0;
        foreach (TemporalPhysicsBody body in trackedBodies)
        {
            if (body == null)
            {
                continue;
            }

            if (body.IsExcludedFromTemporalProjection())
            {
                continue;
            }

            if (statesById.TryGetValue(body.ObjectId, out TemporalObjectState objectState))
            {
                body.ApplyState(objectState, includeVelocitiesWhenApplyingFutureState);
                SampleBody(body);
                appliedCount++;
            }
        }

        return appliedCount;
    }

    private void CacheAndBroadcastFutureState(TemporalWorldState futureState)
    {
        if (futureState == null)
        {
            lastStatus = "Projection completed without a future state";
            projectionPending = false;
            return;
        }

        CacheFutureStateJson(futureState.ToJson(false), true);
    }

    private void CacheFutureStateJson(string json, bool broadcast)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            lastStatus = "Future state JSON was empty";
            projectionPending = false;
            return;
        }

        bool duplicateBroadcast = broadcast &&
            !projectionPending &&
            string.Equals(latestFutureStateJson, json, StringComparison.Ordinal);

        latestFutureStateJson = json;
        projectionPending = false;

        if (!broadcast || duplicateBroadcast)
        {
            return;
        }

        projectionCompletionCount++;
        lastStatus = $"Future state ready ({json.Length} chars)";
        OnFutureStateReady?.Invoke(json);
        onFutureStateReady?.Invoke(json);
    }

    private void HandleProjectionCompletedNoArgs()
    {
        if (!TryCacheFutureStateFromProjector())
        {
            projectionPending = false;
            lastStatus = "Projection completed, but no future state payload was found";
        }
    }

    private void HandleProjectionCompletedState(TemporalWorldState futureState)
    {
        CacheAndBroadcastFutureState(futureState);
    }

    private void HandleProjectionCompletedJson(string json)
    {
        CacheFutureStateJson(json, true);
    }

    private void HandleProjectionCompletedObject(object payload)
    {
        if (!TryCacheFutureStateFromPayload(payload) && !TryCacheFutureStateFromProjector())
        {
            projectionPending = false;
            lastStatus = "Projection completed, but payload was not a future state";
        }
    }

    private void HandleProjectionCompletedStateEvent(object sender, TemporalWorldState futureState)
    {
        CacheAndBroadcastFutureState(futureState);
    }

    private void HandleProjectionCompletedJsonEvent(object sender, string json)
    {
        CacheFutureStateJson(json, true);
    }

    private void HandleProjectionCompletedObjectEvent(object sender, object payload)
    {
        HandleProjectionCompletedObject(payload);
    }

    private bool TryCacheFutureStateFromPayload(object payload)
    {
        if (payload == null)
        {
            return false;
        }

        if (payload is TemporalWorldState futureState)
        {
            CacheAndBroadcastFutureState(futureState);
            return true;
        }

        if (payload is string json)
        {
            CacheFutureStateJson(json, true);
            return true;
        }

        if (TryReadFutureStateMember(payload, out TemporalWorldState nestedState))
        {
            CacheAndBroadcastFutureState(nestedState);
            return true;
        }

        if (TryReadFutureStateJsonMember(payload, out string nestedJson))
        {
            CacheFutureStateJson(nestedJson, true);
            return true;
        }

        return false;
    }

    private bool TryCacheFutureStateFromProjector()
    {
        EnsureProjectorReference();
        if (temporalPhysicsProjector == null)
        {
            return false;
        }

        if (TryReadFutureStateMember(temporalPhysicsProjector, out TemporalWorldState futureState))
        {
            CacheAndBroadcastFutureState(futureState);
            return true;
        }

        if (TryReadFutureStateJsonMember(temporalPhysicsProjector, out string json))
        {
            CacheFutureStateJson(json, true);
            return true;
        }

        return false;
    }

    private bool TryReadFutureStateMember(object source, out TemporalWorldState futureState)
    {
        futureState = null;
        object value = TryReadNamedMember(source, "LatestFutureState")
            ?? TryReadNamedMember(source, "latestFutureState")
            ?? TryReadNamedMember(source, "FutureState")
            ?? TryReadNamedMember(source, "futureState")
            ?? TryReadNamedMember(source, "ProjectedState")
            ?? TryReadNamedMember(source, "projectedState")
            ?? TryReadNamedMember(source, "WorldState")
            ?? TryReadNamedMember(source, "worldState");

        if (value is TemporalWorldState namedState)
        {
            futureState = namedState;
            return true;
        }

        return false;
    }

    private bool TryReadFutureStateJsonMember(object source, out string json)
    {
        json = null;
        object value = TryReadNamedMember(source, "LatestFutureStateJson")
            ?? TryReadNamedMember(source, "latestFutureStateJson")
            ?? TryReadNamedMember(source, "FutureStateJson")
            ?? TryReadNamedMember(source, "futureStateJson")
            ?? TryReadNamedMember(source, "StateJson")
            ?? TryReadNamedMember(source, "stateJson")
            ?? TryReadNamedMember(source, "Json")
            ?? TryReadNamedMember(source, "json");

        if (value is string namedJson && !string.IsNullOrWhiteSpace(namedJson))
        {
            json = namedJson;
            return true;
        }

        return false;
    }

    private object TryReadNamedMember(object source, string memberName)
    {
        if (source == null || string.IsNullOrEmpty(memberName))
        {
            return null;
        }

        Type sourceType = source.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        PropertyInfo property = sourceType.GetProperty(memberName, flags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(source);
            }
            catch (Exception)
            {
                return null;
            }
        }

        FieldInfo field = sourceType.GetField(memberName, flags);
        if (field != null)
        {
            try
            {
                return field.GetValue(source);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    private void EnsureProjectorReference()
    {
        if (temporalPhysicsProjector != null)
        {
            return;
        }

        MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour != null && behaviour.GetType().Name == "TemporalPhysicsProjector")
            {
                temporalPhysicsProjector = behaviour;
                break;
            }
        }
    }

    private void TrySubscribeToProjector()
    {
        EnsureProjectorReference();

        if (temporalPhysicsProjector == subscribedProjector && subscribedProjectionCompletedDelegate != null)
        {
            return;
        }

        if (subscribedProjector != null)
        {
            UnsubscribeFromProjector();
        }

        if (temporalPhysicsProjector == null)
        {
            return;
        }

        EventInfo projectionCompletedEvent = temporalPhysicsProjector.GetType().GetEvent(
            "OnProjectionCompleted",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (projectionCompletedEvent == null)
        {
            return;
        }

        Delegate handler = CreateProjectionCompletedDelegate(projectionCompletedEvent.EventHandlerType);
        if (handler == null)
        {
            lastStatus = "Unsupported OnProjectionCompleted event signature";
            return;
        }

        MethodInfo addMethod = projectionCompletedEvent.GetAddMethod(true);
        if (addMethod == null)
        {
            lastStatus = "OnProjectionCompleted has no add accessor";
            return;
        }

        try
        {
            addMethod.Invoke(temporalPhysicsProjector, new object[] { handler });
        }
        catch (Exception exception)
        {
            lastStatus = $"Failed to subscribe projector event: {UnwrapReflectionException(exception).Message}";
            return;
        }

        subscribedProjector = temporalPhysicsProjector;
        subscribedProjectionCompletedEvent = projectionCompletedEvent;
        subscribedProjectionCompletedDelegate = handler;
    }

    private void UnsubscribeFromProjector()
    {
        if (subscribedProjector == null || subscribedProjectionCompletedEvent == null || subscribedProjectionCompletedDelegate == null)
        {
            subscribedProjector = null;
            subscribedProjectionCompletedEvent = null;
            subscribedProjectionCompletedDelegate = null;
            return;
        }

        MethodInfo removeMethod = subscribedProjectionCompletedEvent.GetRemoveMethod(true);
        if (removeMethod != null)
        {
            try
            {
                removeMethod.Invoke(subscribedProjector, new object[] { subscribedProjectionCompletedDelegate });
            }
            catch (Exception)
            {
                // Event cleanup should never block disable/destroy.
            }
        }

        subscribedProjector = null;
        subscribedProjectionCompletedEvent = null;
        subscribedProjectionCompletedDelegate = null;
    }

    private Delegate CreateProjectionCompletedDelegate(Type eventHandlerType)
    {
        if (eventHandlerType == null)
        {
            return null;
        }

        MethodInfo invokeMethod = eventHandlerType.GetMethod("Invoke");
        if (invokeMethod == null || invokeMethod.ReturnType != typeof(void))
        {
            return null;
        }

        ParameterInfo[] parameters = invokeMethod.GetParameters();
        if (parameters.Length == 0)
        {
            return TryCreateDelegate(eventHandlerType, nameof(HandleProjectionCompletedNoArgs), Type.EmptyTypes);
        }

        if (parameters.Length == 1)
        {
            Type parameterType = parameters[0].ParameterType;
            if (parameterType == typeof(TemporalWorldState))
            {
                return TryCreateDelegate(eventHandlerType, nameof(HandleProjectionCompletedState), new[] { typeof(TemporalWorldState) });
            }

            if (parameterType == typeof(string))
            {
                return TryCreateDelegate(eventHandlerType, nameof(HandleProjectionCompletedJson), new[] { typeof(string) });
            }

            if (!parameterType.IsValueType)
            {
                return TryCreateDelegate(eventHandlerType, nameof(HandleProjectionCompletedObject), new[] { typeof(object) });
            }
        }

        if (parameters.Length == 2)
        {
            Type payloadType = parameters[1].ParameterType;
            if (payloadType == typeof(TemporalWorldState))
            {
                return TryCreateDelegate(eventHandlerType, nameof(HandleProjectionCompletedStateEvent), new[] { typeof(object), typeof(TemporalWorldState) });
            }

            if (payloadType == typeof(string))
            {
                return TryCreateDelegate(eventHandlerType, nameof(HandleProjectionCompletedJsonEvent), new[] { typeof(object), typeof(string) });
            }

            if (!payloadType.IsValueType)
            {
                return TryCreateDelegate(eventHandlerType, nameof(HandleProjectionCompletedObjectEvent), new[] { typeof(object), typeof(object) });
            }
        }

        return null;
    }

    private Delegate TryCreateDelegate(Type eventHandlerType, string methodName, Type[] parameterTypes)
    {
        MethodInfo method = GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            parameterTypes,
            null);

        if (method == null)
        {
            return null;
        }

        try
        {
            return Delegate.CreateDelegate(eventHandlerType, this, method, false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ResampleTrackedBodies()
    {
        PruneBodySamples();
        foreach (TemporalPhysicsBody body in trackedBodies)
        {
            SampleBody(body);
        }
    }

    private void SampleBody(TemporalPhysicsBody body)
    {
        if (body == null)
        {
            return;
        }

        Rigidbody bodyRigidbody = body.Rigidbody != null ? body.Rigidbody : body.GetComponent<Rigidbody>();
        bodySamples[body] = new BodySample
        {
            position = bodyRigidbody != null ? bodyRigidbody.position : body.transform.position
        };
    }

    private void PruneBodySamples()
    {
        for (int i = trackedBodies.Count - 1; i >= 0; i--)
        {
            if (trackedBodies[i] == null)
            {
                trackedBodies.RemoveAt(i);
            }
        }

        List<TemporalPhysicsBody> staleBodies = null;
        foreach (TemporalPhysicsBody sampledBody in bodySamples.Keys)
        {
            if (sampledBody == null || !trackedBodies.Contains(sampledBody))
            {
                if (staleBodies == null)
                {
                    staleBodies = new List<TemporalPhysicsBody>();
                }

                staleBodies.Add(sampledBody);
            }
        }

        if (staleBodies == null)
        {
            return;
        }

        foreach (TemporalPhysicsBody staleBody in staleBodies)
        {
            bodySamples.Remove(staleBody);
        }
    }

    private Exception UnwrapReflectionException(Exception exception)
    {
        return exception is TargetInvocationException targetInvocationException && targetInvocationException.InnerException != null
            ? targetInvocationException.InnerException
            : exception;
    }

    private void OnGUI()
    {
        if (!showDebugGui)
        {
            return;
        }

        Rect debugRect = new Rect(debugGuiPosition.x, debugGuiPosition.y, debugGuiSize.x, debugGuiSize.y);
        GUILayout.BeginArea(debugRect, "Past/Future Timeline", GUI.skin.window);
        GUILayout.Label($"Role: {role}");
        GUILayout.Label($"Tracked bodies: {trackedBodies.Count}");
        GUILayout.Label($"Projector: {(temporalPhysicsProjector != null ? temporalPhysicsProjector.GetType().Name : "Not found")}");
        GUILayout.Label($"Requests: {projectionRequestCount}  Completed: {projectionCompletionCount}");
        GUILayout.Label($"Received: {receivedFutureStateCount}  Applied: {appliedFutureStateCount}");
        GUILayout.Label($"Latest JSON: {(string.IsNullOrEmpty(latestFutureStateJson) ? "None" : latestFutureStateJson.Length + " chars")}");
        GUILayout.Label($"Reason: {lastProjectionReason}");
        GUILayout.Label(projectionPending ? $"Pending: {Time.time - projectionPendingSince:0.0}s" : $"Status: {lastStatus}");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh Bodies"))
        {
            RefreshTrackedBodies();
            ResampleTrackedBodies();
        }

        if (role == TemporalPlayerRole.PastHost && GUILayout.Button("Request Projection"))
        {
            RequestProjection("GUI button", true);
        }
        GUILayout.EndHorizontal();

        if (role == TemporalPlayerRole.FutureClient && GUILayout.Button("Apply Cached Future State"))
        {
            ReceiveFutureStateJson(latestFutureStateJson);
        }

        GUILayout.EndArea();
    }
}
