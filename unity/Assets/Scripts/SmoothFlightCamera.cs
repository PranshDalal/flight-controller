using UnityEngine;

// Third-person chase camera: follows behind/above the target and banks softly with it,
// using Quaternion.Slerp so rotation lags the aircraft instead of snapping every frame.
public class SmoothFlightCamera : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("The airplane transform to follow.")]
    [SerializeField] private Transform target;

    [Header("Chase Offset (relative to target's heading/pitch - roll excluded)")]
    [SerializeField] private Vector3 positionOffset = new Vector3(0f, 4f, -12f);

    [Header("Look Ahead (relative to target's heading/pitch - roll excluded)")]
    [Tooltip("Where the camera aims, offset from the target - keeps the horizon readable instead of staring at the fuselage.")]
    [SerializeField] private Vector3 lookAtOffset = new Vector3(0f, 1.5f, 10f);

    [Header("Damping")]
    [Tooltip("Higher = camera position catches up to the aircraft faster.")]
    [SerializeField] private float positionDamping = 6f;

    [Tooltip("Higher = camera rotation catches up to the aircraft faster. Lower values emphasize banking momentum.")]
    [SerializeField] private float rotationDamping = 4f;

    [Header("Banking Feel")]
    [Tooltip("0 = camera never rolls with the aircraft (rock-steady). 1 = camera fully matches the aircraft's bank angle. Kept low so a hard roll doesn't fling the camera around; the orbit position itself never uses roll at all.")]
    [SerializeField, Range(0f, 1f)] private float rollInfluence = 0.25f;

    /// <summary>Allows a bootstrapper/spawner to wire the chase target without exposing the serialized field.</summary>
    public void SetTarget(Transform newTarget) => target = newTarget;

    /// <summary>Immediately places the camera at its ideal chase position, skipping the damped glide-in (use on spawn/respawn).</summary>
    public void SnapToTarget()
    {
        if (target == null) return;
        transform.SetPositionAndRotation(DesiredPosition(), DesiredRotation());
    }

    private void LateUpdate()
    {
        if (target == null) return;

        float posT = 1f - Mathf.Exp(-positionDamping * Time.deltaTime);
        float rotT = 1f - Mathf.Exp(-rotationDamping * Time.deltaTime);

        transform.position = Vector3.Lerp(transform.position, DesiredPosition(), posT);
        transform.rotation = Quaternion.Slerp(transform.rotation, DesiredRotation(), rotT);
    }

    // Heading + pitch with roll stripped out (minimal rotation to the aircraft's forward, no
    // twist). Unlike LookRotation(forward, worldUp) this has no singularity going through
    // vertical, so it won't flip the camera during a steep climb or loop.
    private Quaternion FollowFrame() => Quaternion.FromToRotation(Vector3.forward, target.forward);

    private Vector3 DesiredPosition() => target.position + FollowFrame() * positionOffset;

    private Quaternion DesiredRotation()
    {
        Quaternion followFrame = FollowFrame();
        Vector3 lookAtPoint = target.position + followFrame * lookAtOffset;
        Vector3 lookDirection = lookAtPoint - DesiredPosition();

        if (lookDirection.sqrMagnitude < 0.0001f)
            return transform.rotation;

        // "Level" up - always perpendicular to forward by construction, so it can't go parallel
        // to lookDirection even in a vertical climb. Blend in a bit of real roll for banking feel.
        Vector3 levelUp = followFrame * Vector3.up;
        Vector3 up = Vector3.Slerp(levelUp, target.up, rollInfluence);
        return Quaternion.LookRotation(lookDirection.normalized, up);
    }
}
