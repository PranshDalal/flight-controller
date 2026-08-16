using System.Collections;
using UnityEngine;

// Turns a hard impact into an explosion. Attached to the airplane root by WorldBootstrapper.
[RequireComponent(typeof(Rigidbody))]
public class CrashEffects : MonoBehaviour
{
    // Measured along the contact normal, not raw relativeVelocity.magnitude - a normal landing at
    // cruise speed has a relativeVelocity dominated by horizontal motion, which would trip a
    // total-speed threshold on every touchdown. This isolates how hard it actually hit.
    [Tooltip("Impact speed (m/s) along the surface normal above which a collision counts as a crash instead of a landing.")]
    [SerializeField] private float crashImpactSpeedThreshold = 10f;

    [Tooltip("Seconds after a crash before the aircraft resets to its spawn point. 0 disables auto-respawn.")]
    [SerializeField] private float respawnDelay = 3f;

    private Rigidbody _rigidbody;
    private AerodynamicFlightController _flightController;
    private EngineStartup _engineStartup;
    private TakeoffClearance _takeoffClearance;
    private Renderer[] _renderers;
    private Vector3 _spawnPosition;
    private Quaternion _spawnRotation;
    private bool _hasCrashed;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _flightController = GetComponent<AerodynamicFlightController>();
        _engineStartup = GetComponent<EngineStartup>();
        _takeoffClearance = GetComponent<TakeoffClearance>();
        _renderers = GetComponentsInChildren<Renderer>();
        _spawnPosition = transform.position;
        _spawnRotation = transform.rotation;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (_hasCrashed) return;

        ContactPoint contact = collision.GetContact(0);
        float impactSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
        if (impactSpeed < crashImpactSpeedThreshold) return;

        Crash(contact.point);
    }

    private void Crash(Vector3 impactPoint)
    {
        _hasCrashed = true;

        ExplosionEffect.Spawn(impactPoint);
        FindObjectOfType<SmoothFlightCamera>()?.Shake(0.4f, 0.6f);

        foreach (Renderer r in _renderers) r.enabled = false;
        if (_flightController != null) _flightController.enabled = false;

        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
        _rigidbody.isKinematic = true;

        if (respawnDelay > 0f) StartCoroutine(RespawnAfterDelay());
    }

    private IEnumerator RespawnAfterDelay()
    {
        yield return new WaitForSeconds(respawnDelay);

        transform.SetPositionAndRotation(_spawnPosition, _spawnRotation);
        _rigidbody.isKinematic = false;
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;

        foreach (Renderer r in _renderers) r.enabled = true;
        if (_flightController != null) _flightController.enabled = true;
        if (_engineStartup != null) _engineStartup.Replay();
        if (_takeoffClearance != null) _takeoffClearance.Play();

        _hasCrashed = false;
    }
}
