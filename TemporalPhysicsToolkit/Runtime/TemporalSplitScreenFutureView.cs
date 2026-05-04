using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class TemporalSplitScreenFutureView : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Camera pastCamera;
    [SerializeField] private Camera futureCamera;
    [SerializeField] private PastFutureTimelineController timelineController;

    [Header("Split Screen")]
    [Range(0.1f, 0.9f)]
    [SerializeField] private float pastViewWidth = 0.5f;
    [SerializeField] private string futureLayerName = "FutureView";
    [SerializeField] private bool copyPastCameraPose = true;
    [SerializeField] private bool mirrorCurrentWorldOnStart = true;

    [Header("Future Scene")]
    [SerializeField] private string futureSceneName = "TemporalFutureView";
    [SerializeField] private bool includeInactiveObjects;

    private readonly Dictionary<string, TemporalPhysicsBody> futureBodiesById =
        new Dictionary<string, TemporalPhysicsBody>();

    private Scene futureScene;
    private int futureLayer = -1;
    private int previousPastCameraMask;
    private bool hasPreviousPastCameraMask;
    private string lastAppliedFutureStateJson;

    public int FutureBodyCount => futureBodiesById.Count;
    public int LastAppliedFutureObjectCount { get; private set; }
    public int LastAppliedFutureFrame { get; private set; } = -1;
    public string LastAppliedFutureStateJson => lastAppliedFutureStateJson;

    private void Awake()
    {
        ResolveReferences();
        EnsureFutureScene();
        RebuildFutureScene();
        ConfigureCameras();
    }

    private void OnEnable()
    {
        ResolveReferences();
        if (timelineController != null)
        {
            timelineController.OnFutureStateReady += ApplyFutureStateJson;
        }

        ApplyLatestFutureStateIfChanged();
    }

    private void Start()
    {
        if (mirrorCurrentWorldOnStart)
        {
            MirrorCurrentWorldState();
        }
    }

    private void LateUpdate()
    {
        ConfigureCameras();

        if (copyPastCameraPose && pastCamera != null && futureCamera != null)
        {
            futureCamera.transform.SetPositionAndRotation(
                pastCamera.transform.position,
                pastCamera.transform.rotation);
        }

        ApplyLatestFutureStateIfChanged();
    }

    private void OnDisable()
    {
        if (timelineController != null)
        {
            timelineController.OnFutureStateReady -= ApplyFutureStateJson;
        }

        if (pastCamera != null)
        {
            pastCamera.cullingMask = previousPastCameraMask;
        }
    }

    private void OnDestroy()
    {
        if (futureScene.IsValid())
        {
            SceneManager.UnloadSceneAsync(futureScene);
        }
    }

    public void ApplyFutureStateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        TemporalWorldState state = TemporalWorldState.FromJson(json);
        ApplyFutureState(state);
        lastAppliedFutureStateJson = json;
    }

    public void ApplyFutureState(TemporalWorldState state)
    {
        if (state == null || state.objects == null)
        {
            return;
        }

        EnsureFutureScene();
        if (futureBodiesById.Count == 0)
        {
            RebuildFutureScene();
        }

        int appliedCount = ApplyFutureObjects(state);
        if (appliedCount < state.objects.Count)
        {
            RebuildFutureScene();
            appliedCount = ApplyFutureObjects(state);
        }

        LastAppliedFutureObjectCount = appliedCount;
        LastAppliedFutureFrame = Time.frameCount;
    }

    private int ApplyFutureObjects(TemporalWorldState state)
    {
        int appliedCount = 0;
        foreach (TemporalObjectState objectState in state.objects)
        {
            if (objectState == null || string.IsNullOrWhiteSpace(objectState.objectId))
            {
                continue;
            }

            if (futureBodiesById.TryGetValue(objectState.objectId, out TemporalPhysicsBody futureBody) &&
                futureBody != null)
            {
                futureBody.ApplyState(objectState, false);
                appliedCount++;
            }
        }

        return appliedCount;
    }

    public void RebuildFutureScene()
    {
        EnsureFutureScene();
        futureBodiesById.Clear();

        GameObject[] roots = futureScene.GetRootGameObjects();
        foreach (GameObject root in roots)
        {
            Destroy(root);
        }

        Scene sourceScene = gameObject.scene.IsValid() ? gameObject.scene : SceneManager.GetActiveScene();
        TemporalPhysicsBody.EnsureAllRigidbodiesHaveTemporalBodies(sourceScene, includeInactiveObjects);
        foreach (GameObject root in sourceScene.GetRootGameObjects())
        {
            if (ShouldSkipRoot(root))
            {
                continue;
            }

            if (root.GetComponentInChildren<TemporalPhysicsBody>(includeInactiveObjects) != null)
            {
                CloneTemporalRoot(root);
                continue;
            }

            if (root.GetComponentInChildren<Renderer>(includeInactiveObjects) != null ||
                root.GetComponentInChildren<Collider>(includeInactiveObjects) != null)
            {
                CloneStaticRoot(root);
            }
        }
    }

    private void ResolveReferences()
    {
        if (pastCamera == null)
        {
            pastCamera = Camera.main;
        }

        if (timelineController == null)
        {
            timelineController = FindAnyObjectByType<PastFutureTimelineController>();
        }
    }

    private void ApplyLatestFutureStateIfChanged()
    {
        ResolveReferences();
        if (timelineController == null)
        {
            return;
        }

        string latestJson = timelineController.LatestFutureStateJson;
        if (string.IsNullOrWhiteSpace(latestJson) ||
            string.Equals(latestJson, lastAppliedFutureStateJson, System.StringComparison.Ordinal))
        {
            return;
        }

        ApplyFutureStateJson(latestJson);
    }

    private void EnsureFutureScene()
    {
        if (!futureScene.IsValid())
        {
            CreateSceneParameters parameters = new CreateSceneParameters(LocalPhysicsMode.Physics3D);
            futureScene = SceneManager.CreateScene(futureSceneName + "_" + Time.frameCount, parameters);
        }

        futureLayer = LayerMask.NameToLayer(futureLayerName);
        if (futureLayer < 0)
        {
            futureLayer = LayerMask.NameToLayer("DelayedPhysics");
        }
    }

    private void ConfigureCameras()
    {
        if (pastCamera == null)
        {
            pastCamera = Camera.main;
        }

        if (futureCamera == null)
        {
            GameObject cameraObject = new GameObject("Future View Camera");
            futureCamera = cameraObject.AddComponent<Camera>();
            CopyCameraSettings(pastCamera, futureCamera);
        }

        if (pastCamera != null)
        {
            pastCamera.rect = new Rect(0f, 0f, pastViewWidth, 1f);
            if (!hasPreviousPastCameraMask)
            {
                previousPastCameraMask = pastCamera.cullingMask;
                hasPreviousPastCameraMask = true;
            }

            if (futureLayer >= 0)
            {
                pastCamera.cullingMask = previousPastCameraMask & ~(1 << futureLayer);
            }
        }

        futureCamera.rect = new Rect(pastViewWidth, 0f, 1f - pastViewWidth, 1f);
        futureCamera.depth = pastCamera != null ? pastCamera.depth + 1f : 1f;
        if (futureLayer >= 0)
        {
            futureCamera.cullingMask = 1 << futureLayer;
        }
    }

    private void CopyCameraSettings(Camera source, Camera target)
    {
        if (source == null || target == null)
        {
            return;
        }

        target.fieldOfView = source.fieldOfView;
        target.nearClipPlane = source.nearClipPlane;
        target.farClipPlane = source.farClipPlane;
        target.clearFlags = source.clearFlags;
        target.backgroundColor = source.backgroundColor;
        target.allowHDR = source.allowHDR;
        target.allowMSAA = source.allowMSAA;
        target.useOcclusionCulling = source.useOcclusionCulling;
    }

    private bool ShouldSkipRoot(GameObject root)
    {
        if (root == null || root == gameObject)
        {
            return true;
        }

        return root.GetComponentInChildren<Camera>(includeInactiveObjects) != null ||
               root.GetComponentInChildren<TemporalSplitScreenFutureView>(includeInactiveObjects) != null ||
               root.GetComponentInChildren<PastFutureTimelineController>(includeInactiveObjects) != null ||
               root.GetComponentInChildren<TemporalPhysicsProjector>(includeInactiveObjects) != null ||
               root.GetComponentInChildren<TemporalPastPlayerController>(includeInactiveObjects) != null;
    }

    private void CloneTemporalRoot(GameObject root)
    {
        GameObject clone = Instantiate(root);
        clone.name = root.name + " (Future)";
        SceneManager.MoveGameObjectToScene(clone, futureScene);
        PrepareFutureClone(clone, true);
    }

    private void CloneStaticRoot(GameObject root)
    {
        if (ContainsNonKinematicRigidbody(root))
        {
            return;
        }

        GameObject clone = Instantiate(root);
        clone.name = root.name + " (Future Static)";
        SceneManager.MoveGameObjectToScene(clone, futureScene);
        PrepareFutureClone(clone, false);
    }

    private void PrepareFutureClone(GameObject cloneRoot, bool collectTemporalBodies)
    {
        SetLayerRecursively(cloneRoot, futureLayer);
        DisableSideEffects(cloneRoot);

        Rigidbody[] rigidbodies = cloneRoot.GetComponentsInChildren<Rigidbody>(true);
        foreach (Rigidbody rb in rigidbodies)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.useGravity = false;
            rb.isKinematic = true;
        }

        if (!collectTemporalBodies)
        {
            return;
        }

        TemporalPhysicsBody[] bodies = cloneRoot.GetComponentsInChildren<TemporalPhysicsBody>(true);
        foreach (TemporalPhysicsBody body in bodies)
        {
            if (body != null &&
                !body.IsExcludedFromTemporalProjection() &&
                !futureBodiesById.ContainsKey(body.ObjectId))
            {
                futureBodiesById.Add(body.ObjectId, body);
            }
        }
    }

    private void MirrorCurrentWorldState()
    {
        Scene sourceScene = gameObject.scene.IsValid() ? gameObject.scene : SceneManager.GetActiveScene();
        TemporalPhysicsBody.EnsureAllRigidbodiesHaveTemporalBodies(sourceScene, includeInactiveObjects);

        TemporalPhysicsBody[] sourceBodies = FindObjectsByType<TemporalPhysicsBody>(
            includeInactiveObjects ? FindObjectsInactive.Include : FindObjectsInactive.Exclude);

        TemporalWorldState state = new TemporalWorldState();
        foreach (TemporalPhysicsBody body in sourceBodies)
        {
            if (body == null || body.gameObject.scene != sourceScene)
            {
                continue;
            }

            if (body.IsExcludedFromTemporalProjection())
            {
                continue;
            }

            state.objects.Add(body.CaptureState());
        }

        ApplyFutureState(state);
    }

    private bool ContainsNonKinematicRigidbody(GameObject root)
    {
        Rigidbody[] rigidbodies = root.GetComponentsInChildren<Rigidbody>(includeInactiveObjects);
        foreach (Rigidbody rb in rigidbodies)
        {
            if (rb != null && !rb.isKinematic)
            {
                return true;
            }
        }

        return false;
    }

    private void DisableSideEffects(GameObject cloneRoot)
    {
        MonoBehaviour[] behaviours = cloneRoot.GetComponentsInChildren<MonoBehaviour>(true);
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null || behaviour is TemporalPhysicsBody)
            {
                continue;
            }

            behaviour.enabled = false;
        }
    }

    private void SetLayerRecursively(GameObject target, int layer)
    {
        if (target == null || layer < 0)
        {
            return;
        }

        target.layer = layer;
        foreach (Transform child in target.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
}
