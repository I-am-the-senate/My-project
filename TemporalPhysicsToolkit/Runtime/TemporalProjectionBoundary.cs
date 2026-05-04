using UnityEngine;

[DisallowMultipleComponent]
public sealed class TemporalProjectionBoundary : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        DeactivateTemporalBody(other);
    }

    private void OnTriggerStay(Collider other)
    {
        DeactivateTemporalBody(other);
    }

    private static void DeactivateTemporalBody(Collider other)
    {
        if (other == null)
        {
            return;
        }

        TemporalPhysicsBody body = other.GetComponentInParent<TemporalPhysicsBody>();
        if (body == null || !body.gameObject.activeSelf)
        {
            return;
        }

        Rigidbody rb = body.Rigidbody != null
            ? body.Rigidbody
            : body.GetComponent<Rigidbody>();

        if (rb != null && !rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.Sleep();
        }

        body.gameObject.SetActive(false);
    }
}
