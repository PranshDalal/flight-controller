using System.Collections;
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

    [Header("View Distance")]
    [Tooltip("Grow the far clip plane with height so the ground stays visible at cruise altitude.")]
    [SerializeField] private bool altitudeFarClip = true;

    [Tooltip("Far clip (m) on the ground.")]
    [SerializeField] private float minFarClip = 1000f;

    [Tooltip("Upper bound on the far clip (m). 400 km is the horizon distance from about 12.5 km (FL410).")]
    [SerializeField] private float maxFarClip = 400000f;

    private Camera _camera;

    private Vector3 _smoothedPosition;
    private Vector3 _shakeOffset;
    private Coroutine _shakeCoroutine;

    /// <summary>Allows a bootstrapper/spawner to wire the chase target without exposing the serialized field.</summary>
    public void SetTarget(Transform newTarget) => target = newTarget;

    /// <summary>Immediately places the camera at its ideal chase position, skipping the damped glide-in (use on spawn/respawn).</summary>
    public void SnapToTarget()
    {
        if (target == null) return;
        _smoothedPosition = DesiredPosition();
        transform.SetPositionAndRotation(_smoothedPosition, DesiredRotation());
    }

    /// <summary>Punches the camera with a brief, decaying random offset - e.g. on a crash impact.</summary>
    public void Shake(float duration, float magnitude)
    {
        if (_shakeCoroutine != null) StopCoroutine(_shakeCoroutine);
        _shakeCoroutine = StartCoroutine(ShakeRoutine(duration, magnitude));
    }

    private IEnumerator ShakeRoutine(float duration, float magnitude)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            float falloff = 1f - elapsed / duration;
            _shakeOffset = Random.insideUnitSphere * magnitude * falloff;
            elapsed += Time.deltaTime;
            yield return null;
        }
        _shakeOffset = Vector3.zero;
    }

    private void LateUpdate()
    {
        if (target == null) return;

        UpdateViewDistance();

        float posT = 1f - Mathf.Exp(-positionDamping * Time.deltaTime);
        float rotT = 1f - Mathf.Exp(-rotationDamping * Time.deltaTime);

        _smoothedPosition = Vector3.Lerp(_smoothedPosition, DesiredPosition(), posT);
        transform.position = _smoothedPosition + _shakeOffset;
        transform.rotation = Quaternion.Slerp(transform.rotation, DesiredRotation(), rotT);
    }

    // Far clip = distance to the horizon for the camera's height above the ground,
    // d = sqrt(2 * R_earth * h), so what's visible scales the way it would from a real cockpit.
    // The near plane moves out with it (far/near stays ~1e5) to keep depth precision.
    private void UpdateViewDistance()
    {
        if (!altitudeFarClip) return;
        if (_camera == null) _camera = GetComponent<Camera>();
        if (_camera == null) return;

        const float earthRadius = 6371000f;
        float height = Mathf.Max(0f, transform.position.y);
        float far = Mathf.Clamp(minFarClip + Mathf.Sqrt(2f * earthRadius * height), minFarClip, maxFarClip);
        _camera.farClipPlane = far;
        _camera.nearClipPlane = Mathf.Clamp(far * 1e-5f, 0.3f, 4f);
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
