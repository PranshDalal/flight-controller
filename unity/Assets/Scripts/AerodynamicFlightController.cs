using UnityEngine;

// Simplified but aerodynamically-grounded flight model: lift/drag come from angle of attack (AoA)
// and airspeed via the standard dynamic-pressure equations (L = q*S*CL, D = q*S*CD), using a
// flat-plate CL/CD curve (CL = liftSlope * sin(2*AoA)) that gives stall behavior for free without
// a hand-authored curve. Thrust is throttle-driven, and control authority scales with airspeed so
// the aircraft can't be flipped in place on the runway.
[RequireComponent(typeof(Rigidbody))]
public class AerodynamicFlightController : MonoBehaviour
{
    // IFlightInput can't be a [SerializeField] (plain interface, no Inspector support) - wired via
    // SetFlightInput() from WorldBootstrapper, with a FindObjectOfType fallback below.
    private IFlightInput flightInput;

    [Header("Aircraft Properties")]
    [Tooltip("Wing reference area (m^2). Scales both lift and drag together.")]
    [SerializeField] private float wingArea = 16f;

    [Tooltip("Air density (kg/m^3). 1.225 = sea level.")]
    [SerializeField] private float airDensity = 1.225f;

    [Header("Lift  (CL = liftSlope * sin(2 * AoA))")]
    [Tooltip("Lift-curve slope. sin(2*AoA) peaks at 45 degrees, so this is roughly the max lift coefficient - rises near-linearly at small AoA, stalls past the peak, no branching needed. Keep it low (~0.5-0.8) - lift scales with speed squared, so this gets punchy fast.")]
    [SerializeField] private float liftSlope = 0.55f;

    // Real wings sit at a slight positive incidence so they still lift while the fuselage is
    // level on the runway - without it, a level aircraft has zero AoA at any speed, and since the
    // flat-bottomed collider resists tipping, it can never build enough AoA to take off on its own.
    [Tooltip("Wing incidence angle (degrees), added on top of the fuselage's own AoA. Lets the aircraft take off level instead of needing to rotate against the ground first.")]
    [SerializeField] private float wingIncidenceDegrees = 6f;

    [Header("Drag  (CD = parasiticDrag + inducedDragFactor * sin(AoA)^2)")]
    [Tooltip("Baseline skin-friction/form drag at zero AoA. Also what makes cutting the throttle actually feel like braking.")]
    [SerializeField] private float parasiticDrag = 0.05f;

    [Tooltip("Extra drag as AoA grows away from zero - combined induced + stall drag.")]
    [SerializeField] private float inducedDragFactor = 0.6f;

    [Header("Propulsion")]
    [Tooltip("Max engine thrust (N) at full throttle. Actual thrust scales with Throttle, which starts at 0.")]
    [SerializeField] private float maxThrust = 6000f;

    [Header("Control Surfaces")]
    [Tooltip("Angular acceleration (rad/s^2) per unit of PitchInput at full authority.")]
    [SerializeField] private float pitchSensitivity = 3.5f;

    [Tooltip("Angular acceleration (rad/s^2) per unit of RollInput at full authority.")]
    [SerializeField] private float rollSensitivity = 4.5f;

    [Tooltip("Angular acceleration (rad/s^2) per unit of YawInput at full authority.")]
    [SerializeField] private float yawSensitivity = 2f;

    [Tooltip("Weathervane yaw stability: torque turning the nose back toward the direction of travel on sideslip. Combined with lift tilting sideways when banked, this is what produces a coordinated turn.")]
    [SerializeField] private float yawStability = 1.5f;

    // Without this, pitch is a pure rate command - holding it (exactly how "tilt hand back and
    // hold" works) never settles at an angle, it just keeps rotating until it blows through the
    // lift curve's peak and stalls. This restoring torque, proportional to current pitch, makes a
    // held input settle at a proportional angle instead.
    [Tooltip("Longitudinal static stability: nose-down restoring torque proportional to current pitch angle, so a held input settles at an angle (capped at maxPitchAngleDeg) instead of rotating through the stall.")]
    [SerializeField] private float maxPitchAngleDeg = 20f;

    [Tooltip("Dihedral-style roll stability: torque back toward wings-level, proportional to current bank. Without it a brief roll input permanently resets the resting bank angle instead of settling back level.")]
    [SerializeField] private float rollStability = 1.5f;

    [Header("Angular Rate Damping")]
    [Tooltip("Opposes pitch rate - how strongly the aircraft resists pitching once rotating.")]
    [SerializeField] private float pitchDamping = 2f;

    [Tooltip("Opposes roll rate - how strongly the aircraft resists rolling once rotating.")]
    [SerializeField] private float rollDamping = 2.5f;

    [Tooltip("Opposes yaw rate, on top of weathervane stability.")]
    [SerializeField] private float yawDamping = 1.5f;

    [Tooltip("Airspeed (m/s) at which control surfaces reach full authority. Below this they taper toward zero.")]
    [SerializeField] private float fullControlAirspeed = 30f;

    [Header("Safety Limits")]
    [Tooltip("Hard cap on airspeed (m/s), so no tuning combination can blow up into a runaway velocity.")]
    [SerializeField] private float maxAirspeed = 400f;

    [Tooltip("Caps angular velocity (deg/s) on every axis.")]
    [SerializeField] private float maxAngularVelocityDegPerSec = 120f;

    private Rigidbody _rigidbody;
    private EngineStartup _engineStartup;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _engineStartup = GetComponent<EngineStartup>();
        _rigidbody.useGravity = true;
        _rigidbody.linearDamping = 0f;   // drag is modeled explicitly below; Rigidbody drag would double-count it
        _rigidbody.angularDamping = 0.5f;
        _rigidbody.maxAngularVelocity = maxAngularVelocityDegPerSec * Mathf.Deg2Rad;

        // Interpolate so the rendered transform doesn't snap between fixed-timestep physics
        // steps - at this aircraft's speed that reads as constant vibration otherwise.
        _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;

        if (flightInput == null)
        {
            flightInput = FindObjectOfType<GloveFlightInput>() as IFlightInput
                          ?? FindObjectOfType<GestureFlightInput>() as IFlightInput
                          ?? FindObjectOfType<KeyboardFlightInput>() as IFlightInput;
            if (flightInput == null)
                Debug.LogWarning("[AerodynamicFlightController] No flight input source found in scene; flying with neutral input.");
        }
    }

    /// <summary>Lets a bootstrapper/spawner wire the input source without a serialized field.</summary>
    public void SetFlightInput(IFlightInput input) => flightInput = input;

    private void FixedUpdate()
    {
        Vector3 velocity = _rigidbody.linearVelocity;
        float speed = velocity.magnitude;
        Vector3 localVelocity = transform.InverseTransformDirection(velocity);

        ApplyThrust();

        if (speed > 0.5f) // below this, velocity direction is meaningless and forces are negligible
            ApplyAerodynamics(velocity, speed, localVelocity);

        ApplyControlTorque(speed, localVelocity);
        ClampAirspeed();
    }

    private void ApplyThrust()
    {
        float throttle = flightInput != null ? flightInput.Throttle : 0f;
        float engineReadiness = _engineStartup != null ? _engineStartup.Readiness : 1f;
        _rigidbody.AddRelativeForce(Vector3.forward * (maxThrust * throttle * engineReadiness), ForceMode.Force);
    }

    private void ApplyAerodynamics(Vector3 velocity, float speed, Vector3 localVelocity)
    {
        // angle between the fuselage's forward axis and the relative airflow, plus wing incidence
        float angleOfAttack = Mathf.Atan2(-localVelocity.y, localVelocity.z) + wingIncidenceDegrees * Mathf.Deg2Rad;

        float liftCoefficient = liftSlope * Mathf.Sin(2f * angleOfAttack);
        float sinAlpha = Mathf.Sin(angleOfAttack);
        float dragCoefficient = parasiticDrag + inducedDragFactor * sinAlpha * sinAlpha;

        float dynamicPressure = 0.5f * airDensity * speed * speed;
        float liftForce = dynamicPressure * wingArea * liftCoefficient;
        float dragForce = dynamicPressure * wingArea * dragCoefficient;

        Vector3 velocityDirection = velocity / speed;

        // Lift is perpendicular to the relative wind, in the aircraft's own pitch/roll plane -
        // deriving it from the real velocity direction and transform.right (not world up) means
        // banking naturally tilts lift sideways into a real turn.
        Vector3 liftAxis = Vector3.Cross(velocityDirection, transform.right);

        if (liftAxis.sqrMagnitude > 0.0001f) // degenerate only when flying almost dead sideways
            _rigidbody.AddForce(liftAxis.normalized * liftForce, ForceMode.Force);

        _rigidbody.AddForce(-velocityDirection * dragForce, ForceMode.Force);
    }

    private void ApplyControlTorque(float speed, Vector3 localVelocity)
    {
        float pitchInput = flightInput != null ? flightInput.PitchInput : 0f;
        float rollInput = flightInput != null ? flightInput.RollInput : 0f;
        float yawInput = flightInput != null ? flightInput.YawInput : 0f;

        // control surfaces need airflow, so authority tapers to zero at zero airspeed
        float controlAuthority = Mathf.Clamp01(speed / fullControlAirspeed);

        float pitchTorque = -pitchInput * pitchSensitivity * controlAuthority;

        // transform.forward.y is exactly sin(pitch angle) regardless of roll/yaw, so this needs
        // no separate angle-extraction math. Scaled so full-authority PitchInput=1 settles at
        // maxPitchAngleDeg instead of rotating through the stall.
        float pitchStabilityGain = pitchSensitivity / Mathf.Sin(maxPitchAngleDeg * Mathf.Deg2Rad);
        pitchTorque += pitchStabilityGain * transform.forward.y * controlAuthority;

        float rollTorque = -rollInput * rollSensitivity * controlAuthority;

        // Same trick as pitch stability above: transform.right.y is exactly sin(bank angle)
        // regardless of pitch/yaw. Cross(right, levelRight) gives the axis that rotates right
        // toward level, which for a pure bank always lies along +-forward - dotting with forward
        // reads off the signed correction torque.
        Vector3 right = transform.right;
        Vector3 levelRight = new Vector3(right.x, 0f, right.z);
        if (levelRight.sqrMagnitude > 0.0001f)
        {
            Vector3 restoringAxis = Vector3.Cross(right, levelRight.normalized);
            rollTorque += Vector3.Dot(restoringAxis, transform.forward) * rollStability * controlAuthority;
        }

        // sideslipAngle and its contribution to yawTorque share a sign: velocity drifted left of
        // the nose (negative local X) means the nose needs to yaw left (negative) to catch up.
        float sideslipAngle = Mathf.Atan2(localVelocity.x, localVelocity.z);
        float yawTorque = -yawInput * yawSensitivity * controlAuthority
                         + sideslipAngle * yawStability * controlAuthority;

        Vector3 localAngularVelocity = transform.InverseTransformDirection(_rigidbody.angularVelocity);
        pitchTorque += -localAngularVelocity.x * pitchDamping * controlAuthority;
        rollTorque += -localAngularVelocity.z * rollDamping * controlAuthority;
        yawTorque += -localAngularVelocity.y * yawDamping * controlAuthority;

        _rigidbody.AddRelativeTorque(pitchTorque, yawTorque, rollTorque, ForceMode.Acceleration);
    }

    private void ClampAirspeed()
    {
        if (_rigidbody.linearVelocity.sqrMagnitude > maxAirspeed * maxAirspeed)
            _rigidbody.linearVelocity = _rigidbody.linearVelocity.normalized * maxAirspeed;
    }
}
