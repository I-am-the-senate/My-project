using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(CharacterController))]
public class TemporalPastPlayerController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveSpeed = 4f;
    [SerializeField] private float gravity = -18f;
    [SerializeField] private float rotationSharpness = 16f;
    [SerializeField] private Camera referenceCamera;

    [Header("Physics Interaction")]
    [SerializeField] private float pushStrength = 4f;
    [SerializeField] private float influenceQuietTime = 0.15f;
    [SerializeField] private PastFutureTimelineController timelineController;

    [Header("Pickup")]
    [SerializeField] private float pickupDistance = 2.4f;
    [SerializeField] private float holdDistance = 1.5f;
    [SerializeField] private float holdHeight = 1.1f;
    [SerializeField] private LayerMask pickupMask = ~0;

    private CharacterController controller;
    private Rigidbody heldBody;
    private TemporalPhysicsBody heldTemporalBody;
    private bool heldBodyWasKinematic;
    private bool heldBodyUsedGravity;
    private Vector3 verticalVelocity;
    private bool influenceActive;
    private float lastInfluenceTime = -999f;
    private Vector3 lastHeldPosition;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        ResolveReferences();
    }

    private void Update()
    {
        ResolveReferences();
        MovePlayer();
        HandlePickupInput();
        MoveHeldBody();
        CheckInfluenceQuietPeriod();
    }

    private void OnDisable()
    {
        DropHeldBody("Past player disabled");
    }

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        Rigidbody body = hit.collider.attachedRigidbody;
        if (body == null || body.isKinematic || hit.moveDirection.y < -0.3f)
        {
            return;
        }

        Vector3 pushDirection = new Vector3(hit.moveDirection.x, 0f, hit.moveDirection.z);
        if (pushDirection.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        body.AddForce(pushDirection.normalized * pushStrength, ForceMode.Impulse);
        MarkInfluenceActive("Pushing " + body.name);
    }

    private void ResolveReferences()
    {
        if (referenceCamera == null)
        {
            referenceCamera = Camera.main;
        }

        if (timelineController == null)
        {
            timelineController = FindAnyObjectByType<PastFutureTimelineController>();
        }
    }

    private void MovePlayer()
    {
        Vector2 moveInput = ReadMoveInput();
        Vector3 cameraForward = referenceCamera != null ? referenceCamera.transform.forward : Vector3.forward;
        Vector3 cameraRight = referenceCamera != null ? referenceCamera.transform.right : Vector3.right;
        cameraForward.y = 0f;
        cameraRight.y = 0f;
        cameraForward.Normalize();
        cameraRight.Normalize();

        Vector3 move = cameraForward * moveInput.y + cameraRight * moveInput.x;
        if (move.sqrMagnitude > 1f)
        {
            move.Normalize();
        }

        if (move.sqrMagnitude > 0.0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(move, Vector3.up);
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                1f - Mathf.Exp(-rotationSharpness * Time.deltaTime));
        }

        if (controller.isGrounded && verticalVelocity.y < 0f)
        {
            verticalVelocity.y = -1f;
        }

        verticalVelocity.y += gravity * Time.deltaTime;
        controller.Move((move * moveSpeed + verticalVelocity) * Time.deltaTime);
    }

    private void HandlePickupInput()
    {
        if (!WasInteractPressedThisFrame())
        {
            return;
        }

        if (heldBody != null)
        {
            DropHeldBody("Dropped " + heldBody.name);
        }
        else
        {
            TryPickupBody();
        }
    }

    private void TryPickupBody()
    {
        Ray ray;
        if (referenceCamera != null)
        {
            ray = new Ray(referenceCamera.transform.position, referenceCamera.transform.forward);
        }
        else
        {
            ray = new Ray(transform.position + Vector3.up, transform.forward);
        }

        if (!Physics.Raycast(ray, out RaycastHit hit, pickupDistance, pickupMask, QueryTriggerInteraction.Ignore))
        {
            return;
        }

        Rigidbody body = hit.rigidbody;
        if (body == null || body.isKinematic)
        {
            return;
        }

        heldBody = body;
        heldTemporalBody = body.GetComponent<TemporalPhysicsBody>();
        heldBodyWasKinematic = body.isKinematic;
        heldBodyUsedGravity = body.useGravity;
        body.isKinematic = true;
        body.useGravity = false;
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        lastHeldPosition = body.position;
        MarkInfluenceActive("Picked up " + body.name);
    }

    private void MoveHeldBody()
    {
        if (heldBody == null)
        {
            return;
        }

        Vector3 holdPosition = transform.position + transform.forward * holdDistance + Vector3.up * holdHeight;
        Vector3 previousPosition = heldBody.position;
        heldBody.position = holdPosition;
        heldBody.rotation = Quaternion.Slerp(heldBody.rotation, transform.rotation, 12f * Time.deltaTime);
        lastHeldPosition = previousPosition;
        MarkInfluenceActive("Holding " + heldBody.name);
    }

    private void DropHeldBody(string reason)
    {
        if (heldBody == null)
        {
            return;
        }

        Vector3 releaseVelocity = (heldBody.position - lastHeldPosition) / Mathf.Max(Time.deltaTime, 0.0001f);
        heldBody.isKinematic = heldBodyWasKinematic;
        heldBody.useGravity = heldBodyUsedGravity;
        heldBody.linearVelocity = releaseVelocity;
        heldBody.WakeUp();

        TemporalPhysicsBody droppedBody = heldTemporalBody;
        heldBody = null;
        heldTemporalBody = null;

        if (timelineController != null)
        {
            if (droppedBody != null)
            {
                timelineController.NotifyPastObjectDropped(droppedBody);
            }
            else
            {
                timelineController.NotifyPastInfluenceEnded(reason);
            }
        }

        influenceActive = false;
    }

    private void MarkInfluenceActive(string reason)
    {
        influenceActive = true;
        lastInfluenceTime = Time.time;
        if (timelineController != null)
        {
            timelineController.NotifyPastInfluenceStarted(reason);
        }
    }

    private void CheckInfluenceQuietPeriod()
    {
        if (!influenceActive || heldBody != null)
        {
            return;
        }

        if (Time.time - lastInfluenceTime < influenceQuietTime)
        {
            return;
        }

        influenceActive = false;
        if (timelineController != null)
        {
            timelineController.NotifyPastInfluenceEnded("Past physical influence ended");
        }
    }

    private Vector2 ReadMoveInput()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            Vector2 input = Vector2.zero;
            if (keyboard.aKey.isPressed) input.x -= 1f;
            if (keyboard.dKey.isPressed) input.x += 1f;
            if (keyboard.sKey.isPressed) input.y -= 1f;
            if (keyboard.wKey.isPressed) input.y += 1f;
            return input;
        }
#endif
        return new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
    }

    private bool WasInteractPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            return keyboard.eKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame;
        }
#endif
        return Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space);
    }
}
