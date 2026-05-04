using UnityEngine;

[DisallowMultipleComponent]
public class TemporalCameraFollow : MonoBehaviour
{
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 offset = new Vector3(0f, 6f, -7f);
    [SerializeField] private Vector3 lookAtOffset = new Vector3(0f, 1f, 0f);
    [SerializeField] private float positionSharpness = 10f;
    [SerializeField] private float rotationSharpness = 14f;

    private void LateUpdate()
    {
        if (target == null)
        {
            TemporalPastPlayerController player = FindAnyObjectByType<TemporalPastPlayerController>();
            if (player != null)
            {
                target = player.transform;
            }
        }

        if (target == null)
        {
            return;
        }

        Vector3 desiredPosition = target.position + offset;
        transform.position = Vector3.Lerp(
            transform.position,
            desiredPosition,
            1f - Mathf.Exp(-positionSharpness * Time.deltaTime));

        Vector3 lookTarget = target.position + lookAtOffset;
        Vector3 toTarget = lookTarget - transform.position;
        if (toTarget.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        Quaternion desiredRotation = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            desiredRotation,
            1f - Mathf.Exp(-rotationSharpness * Time.deltaTime));
    }
}
