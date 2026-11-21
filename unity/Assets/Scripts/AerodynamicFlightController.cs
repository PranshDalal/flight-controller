using System;
using UnityEngine;

// Aerodynamically grounded flight model: lift/drag come from angle of attack (AoA) and airspeed via
// the standard dynamic-pressure equations (L = q*S*CL, D = q*S*CD). Two selectable presets:
//
//   Arcade    - the original tuning: a flat-plate CL = liftSlope*sin(2*AoA) curve on a 1,000 kg,
//               16 m^2 airplane, instant thrust, airspeed-proportional control authority.
//   Realistic - a Gulfstream G650ER: finite-wing lift slope with a continuous stall that blends into
//               flat-plate lift, CD = CD0 + k*CL^2 plus post-stall and compressibility drag, real
//               mass/thrust/wing geometry, an aircraft-like inertia tensor, engine spool lag, and
//               fly-by-wire-style control authority that scales with dynamic pressure, and the
//               1976 US Standard Atmosphere (ISA): density and speed of sound fall with altitude,
//               and thrust lapses with density and Mach.
//
// G650ER sources (checked 2026-11-21):
//   - Wingspan 30.35 m (99 ft 7 in), rated takeoff thrust 75.20 kN (16,900 lbf) per BR725, MTOW
//     46,992 kg (103,600 lb): Gulfstream G650ER spec sheet / gulfstream.com G650ER page.
//   - Wing area 119.2 m^2 (1,283 ft^2): AIN "Gulfstream unveils G650" (2008-03-25); Wikipedia
//     "Gulfstream G650" specifications (aspect ratio 7.7 listed there; 30.35^2/119.2 = 7.73).
//   - Length 30.40 m, basic operating weight ~24,000 kg, MMO 0.925, cruise M0.85-0.90:
//     Wikipedia "Gulfstream G650" specifications (citing gulfstream.com).
//   - Rolls-Royce/EASA BR725 certification press releases (2009) quote 71.6 kN for the initial
//     BR700-725A1-12 rating; the 75.2 kN G650ER spec-sheet figure is used here.
//   - Engine spool: 14 CFR 33.73 requires 15%->95% takeoff thrust in <= 5 s.
//   - Speed limits VMO 340 KCAS / MMO 0.925: AIN "Gulfstream G650 reaches Mach 0.925" (2010-05-03);
//     MMO also on gulfstream.com and in Aviation Week's G650/G650ER overview.
//   - Atmosphere: U.S. Standard Atmosphere 1976 / ICAO ISA (troposphere lapse -6.5 K/km to
//     11 km, isothermal 216.65 K to 20 km). Certified ceiling 51,000 ft: Gulfstream / Wikipedia.
// Not published (engineering estimates, labeled where used): CD0, Oswald e, CLmax, inertia,
// thrust-lapse exponents.
public enum FlightModelPresetType { Arcade, Realistic }

[Serializable]
public class FlightModelPreset
{
    [Tooltip("Off = original arcade curves. On = finite-wing lift with stall, CD = CD0 + k*CL^2, compressibility drag.")]
    public bool useRealisticAerodynamics;

    [Header("Mass")]
    public float mass;
    [Tooltip("Off = Unity's automatic inertia from the collider. On = use inertiaTensor below.")]
    public bool useCustomInertia;
    [Tooltip("Principal moments of inertia (kg*m^2) in Unity's local axes: x = pitch, y = yaw, z = roll.")]
    public Vector3 inertiaTensor;

    [Header("Wing & Air")]
    [Tooltip("Wing reference area S (m^2).")]
    public float wingArea;
    [Tooltip("Wingspan b (m). Realistic model only: aspect ratio AR = b^2 / S.")]
    public float wingSpan;
    [Tooltip("Air density (kg/m^3): used as-is when the standard atmosphere is off, and as the sea-level reference for control authority. 1.225 = sea level ISA.")]
    public float airDensity;
    [Tooltip("Wing incidence (degrees) added to the fuselage AoA, so the wing still lifts with the fuselage level.")]
    public float wingIncidenceDegrees;

    [Header("Arcade Lift/Drag  (CL = liftSlope*sin(2a), CD = parasitic + induced*sin^2 a)")]
    public float liftSlope;
    public float parasiticDrag;
    public float inducedDragFactor;

    [Header("Realistic Lift/Drag")]
    [Tooltip("Peak lift coefficient at the stall angle. Stall angle = CLmax / (finite-wing lift slope).")]
    public float maxLiftCoefficient;
    [Tooltip("Degrees past the stall angle over which lift blends (smoothly) into the flat-plate curve 2*sin(a)*cos(a).")]
    public float stallBlendDegrees;
    [Tooltip("Zero-lift drag coefficient CD0.")]
    public float zeroLiftDrag;
    [Tooltip("Oswald span efficiency e in k = 1 / (pi * e * AR).")]
    public float oswaldEfficiency;
    [Tooltip("Mach number where compressibility drag has risen by 0.002 (Korn/Lock drag-rise model).")]
    public float dragDivergenceMach;
    [Tooltip("Speed of sound (m/s) when the standard atmosphere is off. 340.3 = sea level ISA.")]
    public float speedOfSound;

    [Header("Atmosphere")]
    [Tooltip("On = air density and speed of sound follow the International Standard Atmosphere at the aircraft's altitude. Off = constant airDensity / speedOfSound.")]
    public bool useStandardAtmosphere;
    [Tooltip("Pressure altitude (m) of world Y = 0, i.e. the runway's elevation.")]
    public float fieldElevation;
    [Tooltip("Real altitude per scene meter of height. 1 = true scale; raise it to reach cruise altitude faster.")]
    public float altitudeScale;
    [Tooltip("Thrust scales with (density / sea-level density)^this. ~0.7 for a high-bypass turbofan.")]
    public float thrustDensityExponent;
    [Tooltip("Thrust scales with (1 - this * Mach): ram drag on a high-bypass fan.")]
    public float thrustMachLapse;

    [Header("Overspeed Protection")]
    [Tooltip("Fly-by-wire-style speed protection: thrust tapers to idle approaching VMO/MMO, and above the limit the nose is pitched up. Dives can still briefly overshoot.")]
    public bool overspeedProtection;
    [Tooltip("Maximum operating speed, knots equivalent airspeed (~calibrated airspeed below Mach 0.5-ish).")]
    public float vmoKnots;
    [Tooltip("Maximum operating Mach number.")]
    public float mmo;
    [Tooltip("Fraction below the limit where thrust starts tapering, and above it where pitch-up reaches full authority.")]
    public float overspeedBand;

    [Header("Propulsion")]
    [Tooltip("Total max thrust (N) at full throttle.")]
    public float maxThrust;
    [Tooltip("First-order engine spool time constant (s). 0 = thrust follows throttle instantly.")]
    public float engineTimeConstant;

    [Header("Control Surfaces")]
    [Tooltip("Off = authority rises linearly with airspeed. On = rises with dynamic pressure (speed^2), like real control surfaces, and holds constant above fullControlAirspeed (fly-by-wire).")]
    public bool dynamicPressureAuthority;
    [Tooltip("Airspeed (m/s) at which control authority reaches full.")]
    public float fullControlAirspeed;
    [Tooltip("While touching the ground, drop the pitch-hold torque (a fly-by-wire 'ground mode') so the nose can be rotated for takeoff.")]
    public bool directLawOnGround;
    [Tooltip("Angular acceleration (rad/s^2) per unit PitchInput at full authority.")]
    public float pitchSensitivity;
    [Tooltip("Angular acceleration (rad/s^2) per unit RollInput at full authority.")]
    public float rollSensitivity;
    [Tooltip("Angular acceleration (rad/s^2) per unit YawInput at full authority.")]
    public float yawSensitivity;
    [Tooltip("Weathervane: yaw acceleration (rad/s^2) per radian of sideslip.")]
    public float yawStability;
    [Tooltip("Pitch angle (deg) a held full PitchInput settles at (sets the pitch-restoring gain).")]
    public float maxPitchAngleDeg;
    [Tooltip("Wing-leveling: roll acceleration (rad/s^2) per unit sin(bank).")]
    public float rollStability;

    [Header("Angular Rate Damping (1/s)")]
    public float pitchDamping;
    public float rollDamping;
    public float yawDamping;
    [Tooltip("Rigidbody.angularDamping, on top of the explicit rate damping.")]
    public float rigidbodyAngularDamping;

    [Header("Safety Limits")]
    [Tooltip("Hard cap on airspeed (m/s).")]
    public float maxAirspeed;
    [Tooltip("Caps angular velocity (deg/s) on every axis.")]
    public float maxAngularVelocityDegPerSec;

    public float AspectRatio => wingSpan * wingSpan / wingArea;
    /// <summary>Finite-wing lift slope a = 2*pi / (1 + 2/AR), per radian.</summary>
    public float LiftSlopePerRad => 2f * Mathf.PI / (1f + 2f / AspectRatio);
    public float StallAngleRad => maxLiftCoefficient / LiftSlopePerRad;
    /// <summary>Induced-drag factor k = 1 / (pi * e * AR).</summary>
    public float InducedDragK => 1f / (Mathf.PI * oswaldEfficiency * AspectRatio);
    /// <summary>1-g stall speed (m/s) at this preset's mass, wing area and air density.</summary>
    // standard g as a constant: this runs from field initializers, where Unity's Physics API can't be called
    public float StallSpeed => Mathf.Sqrt(2f * mass * 9.81f / (airDensity * wingArea * maxLiftCoefficient));

    /// <summary>Real altitude (m) for a world-space height.</summary>
    public float AltitudeAt(float worldY) => fieldElevation + worldY * altitudeScale;

    /// <summary>Air density (kg/m^3) and speed of sound (m/s) at a world-space height.</summary>
    public void Air(float worldY, out float density, out float soundSpeed)
    {
        if (!useStandardAtmosphere)
        {
            density = airDensity;
            soundSpeed = speedOfSound;
            return;
        }
        StandardAtmosphere(AltitudeAt(worldY), out density, out soundSpeed);
    }

    /// <summary>
    /// International Standard Atmosphere (US Standard Atmosphere 1976) up to 20 km: temperature
    /// falls 6.5 K/km to the 11 km tropopause, then stays at 216.65 K.
    /// </summary>
    public static void StandardAtmosphere(float altitudeM, out float density, out float soundSpeed)
    {
        const float T0 = 288.15f, P0 = 101325f, lapse = 0.0065f, R = 287.053f, gamma = 1.4f, g0 = 9.80665f;
        const float hTrop = 11000f, TTrop = 216.65f;
        float h = Mathf.Clamp(altitudeM, -500f, 20000f);
        float T, p;
        if (h <= hTrop)
        {
            T = T0 - lapse * h;
            p = P0 * Mathf.Pow(T / T0, g0 / (lapse * R));
        }
        else
        {
            float pTrop = P0 * Mathf.Pow(TTrop / T0, g0 / (lapse * R));
            T = TTrop;
            p = pTrop * Mathf.Exp(-g0 * (h - hTrop) / (R * TTrop));
        }
        density = p / (R * T);
        soundSpeed = Mathf.Sqrt(gamma * R * T);
    }

    /// <summary>
    /// How close the aircraft is to its speed limit: max(EAS / VMO, Mach / MMO). 1 = at the limit.
    /// EAS = TAS * sqrt(density / sea-level density).
    /// </summary>
    public float SpeedLimitRatio(float trueAirspeed, float density, float mach)
    {
        if (!overspeedProtection) return 0f;
        const float knotsToMs = 0.514444f;
        float equivalentAirspeed = trueAirspeed * Mathf.Sqrt(density / 1.225f);
        return Mathf.Max(equivalentAirspeed / (vmoKnots * knotsToMs), mach / mmo);
    }

    /// <summary>Available thrust as a fraction of sea-level static thrust.</summary>
    public float ThrustLapse(float density, float mach)
    {
        float densityFactor = useStandardAtmosphere ? Mathf.Pow(density / 1.225f, thrustDensityExponent) : 1f;
        return densityFactor * Mathf.Max(0f, 1f - thrustMachLapse * mach);
    }

    /// <summary>Lift and drag coefficients at a wing AoA (radians, -pi..pi) and Mach number.</summary>
    public void Coefficients(float alpha, float mach, out float cl, out float cd)
    {
        if (!useRealisticAerodynamics)
        {
            cl = liftSlope * Mathf.Sin(2f * alpha);
            float s = Mathf.Sin(alpha);
            cd = parasiticDrag + inducedDragFactor * s * s;
            return;
        }

        // 0 below the stall angle, rising smoothly (zero slope at both ends) to 1 at
        // stall + stallBlendDegrees - lift has no step, so there's no jitter at the stall.
        float stallBlend = Mathf.SmoothStep(0f, 1f, (Mathf.Abs(alpha) - StallAngleRad) / (stallBlendDegrees * Mathf.Deg2Rad));
        float attachedLift = Mathf.Clamp(LiftSlopePerRad * alpha, -maxLiftCoefficient, maxLiftCoefficient);
        float flatPlateLift = Mathf.Sin(2f * alpha); // = 2*sin(a)*cos(a)
        cl = Mathf.Lerp(attachedLift, flatPlateLift, stallBlend);

        float sinAlpha = Mathf.Sin(alpha);
        cd = zeroLiftDrag + InducedDragK * cl * cl
           + stallBlend * 2f * sinAlpha * sinAlpha; // separated-flow (flat-plate) drag past the stall

        // Lock's fourth-power drag rise: Mcrit = Mdd - (0.1/80)^(1/3), dCD = 20*(M - Mcrit)^4
        float criticalMach = dragDivergenceMach - Mathf.Pow(0.1f / 80f, 1f / 3f);
        if (mach > criticalMach)
            cd += 20f * Mathf.Pow(mach - criticalMach, 4f);
    }

    /// <summary>The original tuning, value for value.</summary>
    public static FlightModelPreset Arcade() => new FlightModelPreset
    {
        useRealisticAerodynamics = false,
        mass = 1000f,
        useCustomInertia = false,
        wingArea = 16f,
        airDensity = 1.225f,
        wingIncidenceDegrees = 6f,
        liftSlope = 0.55f,
        parasiticDrag = 0.05f,
        inducedDragFactor = 0.6f,
        maxThrust = 6000f,
        engineTimeConstant = 0f,
        dynamicPressureAuthority = false,
        fullControlAirspeed = 30f,
        directLawOnGround = false,
        pitchSensitivity = 3.5f,
        rollSensitivity = 4.5f,
        yawSensitivity = 2f,
        yawStability = 1.5f,
        maxPitchAngleDeg = 20f,
        rollStability = 1.5f,
        pitchDamping = 2f,
        rollDamping = 2.5f,
        yawDamping = 1.5f,
        rigidbodyAngularDamping = 0.5f,
        maxAirspeed = 400f,
        maxAngularVelocityDegPerSec = 120f,
        // unused by the arcade curves; filled so switching the toggle alone stays sane
        wingSpan = 30.35f, maxLiftCoefficient = 1.45f, stallBlendDegrees = 10f, zeroLiftDrag = 0.02f,
        oswaldEfficiency = 0.8f, dragDivergenceMach = 0.9f, speedOfSound = 340.3f,
        // the original model had no atmosphere and no thrust lapse
        useStandardAtmosphere = false, fieldElevation = 0f, altitudeScale = 1f,
        thrustDensityExponent = 0f, thrustMachLapse = 0f,
        overspeedProtection = false, vmoKnots = 340f, mmo = 0.925f, overspeedBand = 0.02f,
    };

    /// <summary>Gulfstream G650ER. Every gain below is derived from aircraft data or a stated handling target.</summary>
    public static FlightModelPreset Realistic()
    {
        // Published aircraft data (sources in the file header)
        const float wingSpanM = 30.35f;
        const float wingAreaM2 = 119.2f;
        const float lengthM = 30.40f;
        const float thrustPerEngineN = 75200f;
        const float mmo = 0.925f;
        const float flyingMassKg = 40000f; // mid-mission: between BOW (~24,000 kg) and MTOW (46,992 kg)

        // Estimated radii of gyration as fractions of the reference length, in the range Roskam
        // (Airplane Design Part V) gives for business jets. Roll uses span, pitch uses length,
        // yaw uses (span + length) / 2.
        const float rollRadiusFrac = 0.29f, pitchRadiusFrac = 0.31f, yawRadiusFrac = 0.42f;

        // Handling targets for a heavy fly-by-wire jet flown with the glove
        const float maxRollRateDeg = 30f;      // full roll input
        const float rollTimeConstantS = 0.5f;  // time to reach that rate
        const float heldBankDeg = 35f;         // bank a held full roll input settles at
        const float maxPitchRateDeg = 6f;      // full pitch input
        const float heldPitchDeg = 12f;        // pitch a held full input settles at (below the stall at rotation)
        const float rudderSideslipDeg = 5f;    // sideslip a held full yaw input holds
        const float weathervaneFrequency = 1.2f;   // rad/s, directional (Dutch-roll-like) mode
        const float weathervaneDampingRatio = 0.7f;

        var p = new FlightModelPreset
        {
            useRealisticAerodynamics = true,
            mass = flyingMassKg,
            useCustomInertia = true,
            wingArea = wingAreaM2,
            wingSpan = wingSpanM,
            airDensity = 1.225f,
            speedOfSound = 340.3f,
            useStandardAtmosphere = true,
            fieldElevation = 0f,         // sea-level runway
            altitudeScale = 1f,          // true scale: FL410 = 12,497 m of scene height
            thrustDensityExponent = 0.7f, // estimate for a high-bypass turbofan
            thrustMachLapse = 0.3f,       // estimate
            overspeedProtection = true,
            vmoKnots = 340f,              // KCAS (treated as equivalent airspeed)
            mmo = mmo,
            overspeedBand = 0.02f,        // taper over the last 2% below the limit
            wingIncidenceDegrees = 2.5f,
            maxLiftCoefficient = 1.45f, // estimate; requested range 1.4-1.6
            stallBlendDegrees = 10f,
            zeroLiftDrag = 0.020f,      // estimate: typical clean business jet 0.018-0.022
            oswaldEfficiency = 0.8f,
            dragDivergenceMach = 0.90f, // the G650's high-speed cruise Mach; MMO is 0.925
            maxThrust = 2f * thrustPerEngineN,
            // first-order lag reaching 15% -> 95% in 5 s: 5 = tau * ln(0.85 / 0.05)
            engineTimeConstant = 5f / Mathf.Log(0.85f / 0.05f),
            dynamicPressureAuthority = true,
            directLawOnGround = true,
            maxPitchAngleDeg = heldPitchDeg,
            rigidbodyAngularDamping = 0.05f, // aero rate damping is modeled explicitly
            maxAirspeed = 1.1f * mmo * 340.3f,
            maxAngularVelocityDegPerSec = 1.5f * maxRollRateDeg,
            // keep the arcade fields filled so the toggle alone still flies
            liftSlope = 0.55f, parasiticDrag = 0.05f, inducedDragFactor = 0.6f,
        };

        float m = p.mass;
        float Sq(float x) => x * x;
        p.inertiaTensor = new Vector3(
            m * Sq(pitchRadiusFrac * lengthM / 2f),                      // x: pitch
            m * Sq(yawRadiusFrac * (wingSpanM + lengthM) / 4f),          // y: yaw
            m * Sq(rollRadiusFrac * wingSpanM / 2f));                    // z: roll

        // Roll: steady rate = sensitivity / damping; damping = 1 / time constant.
        p.rollDamping = 1f / rollTimeConstantS;
        p.rollSensitivity = maxRollRateDeg * Mathf.Deg2Rad * p.rollDamping;
        p.rollStability = p.rollSensitivity / Mathf.Sin(heldBankDeg * Mathf.Deg2Rad);

        // Pitch: enough angular acceleration to rotate the nose on the runway (same as arcade),
        // with damping that caps the steady pitch rate at maxPitchRateDeg.
        p.pitchSensitivity = 3.5f;
        p.pitchDamping = p.pitchSensitivity / (maxPitchRateDeg * Mathf.Deg2Rad);

        // Weathervane as a second-order mode: stiffness = w^2, damping = 2*zeta*w.
        p.yawStability = Sq(weathervaneFrequency);
        p.yawDamping = 2f * weathervaneDampingRatio * weathervaneFrequency;
        p.yawSensitivity = p.yawStability * rudderSideslipDeg * Mathf.Deg2Rad;

        // Full control authority from the stall speed up.
        p.fullControlAirspeed = p.StallSpeed;
        return p;
    }
}

[RequireComponent(typeof(Rigidbody))]
public class AerodynamicFlightController : MonoBehaviour
{
    // IFlightInput can't be a [SerializeField] (plain interface, no Inspector support) - wired via
    // SetFlightInput() from WorldBootstrapper, with a FindObjectOfType fallback below.
    // GestureFlightInput (the glove's on-device gesture classifier) is the only flight input.
    private IFlightInput flightInput;

    [Tooltip("Which preset flies the aircraft. Can be switched live in Play mode.")]
    [SerializeField] private FlightModelPresetType preset = FlightModelPresetType.Realistic;

    [SerializeField] private FlightModelPreset arcadePreset = FlightModelPreset.Arcade();
    [SerializeField] private FlightModelPreset realisticPreset = FlightModelPreset.Realistic();

    private Rigidbody _rigidbody;
    private EngineStartup _engineStartup;
    private FlightModelPreset _appliedPreset;
    private float _thrustFraction;       // spooled engine output, 0..1
    private float _lastGroundContactTime = float.NegativeInfinity;
    private float _speedLimitRatio;      // max(EAS/VMO, M/MMO), refreshed each physics step

    public FlightModelPreset ActivePreset => preset == FlightModelPresetType.Arcade ? arcadePreset : realisticPreset;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _engineStartup = GetComponent<EngineStartup>();
        _rigidbody.useGravity = true;
        _rigidbody.linearDamping = 0f;   // drag is modeled explicitly below; Rigidbody drag would double-count it

        // Interpolate so the rendered transform doesn't snap between fixed-timestep physics
        // steps - at this aircraft's speed that reads as constant vibration otherwise.
        _rigidbody.interpolation = RigidbodyInterpolation.Interpolate;

        ApplyPresetToRigidbody();

        if (flightInput == null)
        {
            flightInput = FindObjectOfType<GestureFlightInput>() as IFlightInput;
            if (flightInput == null)
                Debug.LogWarning("[AerodynamicFlightController] No flight input source found in scene; flying with neutral input.");
        }
    }

    /// <summary>Lets a bootstrapper/spawner wire the input source without a serialized field.</summary>
    public void SetFlightInput(IFlightInput input) => flightInput = input;

    // mass, inertia and rigidbody limits come from the active preset
    private void ApplyPresetToRigidbody()
    {
        FlightModelPreset p = ActivePreset;
        _rigidbody.mass = p.mass;
        if (p.useCustomInertia)
        {
            _rigidbody.inertiaTensor = p.inertiaTensor;
            _rigidbody.inertiaTensorRotation = Quaternion.identity;
        }
        else
        {
            _rigidbody.ResetInertiaTensor();
        }
        _rigidbody.angularDamping = p.rigidbodyAngularDamping;
        _rigidbody.maxAngularVelocity = p.maxAngularVelocityDegPerSec * Mathf.Deg2Rad;
        _appliedPreset = p;
    }

    private void OnCollisionStay(Collision collision) => _lastGroundContactTime = Time.fixedTime;

    private bool IsTouchingGround => Time.fixedTime - _lastGroundContactTime <= 2f * Time.fixedDeltaTime;

    private void FixedUpdate()
    {
        if (_appliedPreset != ActivePreset)
            ApplyPresetToRigidbody(); // preset switched in the Inspector

        Vector3 velocity = _rigidbody.linearVelocity;
        float speed = velocity.magnitude;
        Vector3 localVelocity = transform.InverseTransformDirection(velocity);

        // local air at the aircraft's altitude (constant in the arcade preset)
        ActivePreset.Air(_rigidbody.position.y, out float density, out float soundSpeed);
        float mach = speed / soundSpeed;
        _speedLimitRatio = ActivePreset.SpeedLimitRatio(speed, density, mach);

        ApplyThrust(density, mach);

        if (speed > 0.5f) // below this, velocity direction is meaningless and forces are negligible
            ApplyAerodynamics(velocity, speed, localVelocity, density, mach);

        ApplyControlTorque(speed, localVelocity, density);
        ClampAirspeed();
    }

    private void ApplyThrust(float density, float mach)
    {
        FlightModelPreset p = ActivePreset;
        float throttle = flightInput != null ? flightInput.Throttle : 0f;
        float engineReadiness = _engineStartup != null ? _engineStartup.Readiness : 1f;
        float commanded = throttle * engineReadiness;

        // first-order spool-up/down toward the commanded thrust
        _thrustFraction = p.engineTimeConstant > 0f
            ? Mathf.Lerp(_thrustFraction, commanded, 1f - Mathf.Exp(-Time.fixedDeltaTime / p.engineTimeConstant))
            : commanded;

        // thinner air and higher Mach reduce what the engines can deliver
        float availableThrust = p.maxThrust * p.ThrustLapse(density, mach);

        // overspeed protection: thrust fades to idle over the last overspeedBand below VMO/MMO
        if (p.overspeedProtection)
            availableThrust *= Mathf.Clamp01((1f - _speedLimitRatio) / p.overspeedBand);
        _rigidbody.AddRelativeForce(Vector3.forward * (availableThrust * _thrustFraction), ForceMode.Force);
    }

    private void ApplyAerodynamics(Vector3 velocity, float speed, Vector3 localVelocity, float density, float mach)
    {
        FlightModelPreset p = ActivePreset;

        // angle between the fuselage's forward axis and the relative airflow, plus wing incidence
        float angleOfAttack = Mathf.Atan2(-localVelocity.y, localVelocity.z) + p.wingIncidenceDegrees * Mathf.Deg2Rad;
        if (angleOfAttack > Mathf.PI) angleOfAttack -= 2f * Mathf.PI;

        p.Coefficients(angleOfAttack, mach, out float liftCoefficient, out float dragCoefficient);

        float dynamicPressure = 0.5f * density * speed * speed;
        float liftForce = dynamicPressure * p.wingArea * liftCoefficient;
        float dragForce = dynamicPressure * p.wingArea * dragCoefficient;

        Vector3 velocityDirection = velocity / speed;

        // Lift is perpendicular to the relative wind, in the aircraft's own pitch/roll plane -
        // deriving it from the real velocity direction and transform.right (not world up) means
        // banking naturally tilts lift sideways into a real turn.
        Vector3 liftAxis = Vector3.Cross(velocityDirection, transform.right);

        if (liftAxis.sqrMagnitude > 0.0001f) // degenerate only when flying almost dead sideways
            _rigidbody.AddForce(liftAxis.normalized * liftForce, ForceMode.Force);

        _rigidbody.AddForce(-velocityDirection * dragForce, ForceMode.Force);
    }

    private void ApplyControlTorque(float speed, Vector3 localVelocity, float density)
    {
        FlightModelPreset p = ActivePreset;
        float pitchInput = flightInput != null ? flightInput.PitchInput : 0f;
        float rollInput = flightInput != null ? flightInput.RollInput : 0f;
        float yawInput = flightInput != null ? flightInput.YawInput : 0f;

        // control surfaces need airflow, so authority tapers to zero at zero airspeed - linearly
        // with speed (arcade) or with dynamic pressure (realistic): q / q_full, where q_full is
        // the sea-level dynamic pressure at fullControlAirspeed, so thin air at altitude needs
        // more true airspeed for the same authority
        float speedRatio = speed / p.fullControlAirspeed;
        float controlAuthority = Mathf.Clamp01(p.dynamicPressureAuthority
            ? (density * speed * speed) / (p.airDensity * p.fullControlAirspeed * p.fullControlAirspeed)
            : speedRatio);

        float pitchTorque = -pitchInput * p.pitchSensitivity * controlAuthority;

        // overspeed protection: past the limit, pitch the nose up (negative = nose up), reaching
        // full pitch authority at overspeedBand over the limit, to trade speed for altitude
        if (p.overspeedProtection && _speedLimitRatio > 1f)
            pitchTorque -= p.pitchSensitivity * controlAuthority * Mathf.Clamp01((_speedLimitRatio - 1f) / p.overspeedBand);

        // transform.forward.y is exactly sin(pitch angle) regardless of roll/yaw, so this needs
        // no separate angle-extraction math. Scaled so full-authority PitchInput=1 settles at
        // maxPitchAngleDeg instead of rotating through the stall.
        if (!(p.directLawOnGround && IsTouchingGround))
        {
            float pitchStabilityGain = p.pitchSensitivity / Mathf.Sin(p.maxPitchAngleDeg * Mathf.Deg2Rad);
            pitchTorque += pitchStabilityGain * transform.forward.y * controlAuthority;
        }

        float rollTorque = -rollInput * p.rollSensitivity * controlAuthority;

        // Same trick as pitch stability above: transform.right.y is exactly sin(bank angle)
        // regardless of pitch/yaw. Cross(right, levelRight) gives the axis that rotates right
        // toward level, which for a pure bank always lies along +-forward - dotting with forward
        // reads off the signed correction torque.
        Vector3 right = transform.right;
        Vector3 levelRight = new Vector3(right.x, 0f, right.z);
        if (levelRight.sqrMagnitude > 0.0001f)
        {
            Vector3 restoringAxis = Vector3.Cross(right, levelRight.normalized);
            rollTorque += Vector3.Dot(restoringAxis, transform.forward) * p.rollStability * controlAuthority;
        }

        // sideslipAngle and its contribution to yawTorque share a sign: velocity drifted left of
        // the nose (negative local X) means the nose needs to yaw left (negative) to catch up.
        float sideslipAngle = Mathf.Atan2(localVelocity.x, localVelocity.z);
        float yawTorque = -yawInput * p.yawSensitivity * controlAuthority
                         + sideslipAngle * p.yawStability * controlAuthority;

        Vector3 localAngularVelocity = transform.InverseTransformDirection(_rigidbody.angularVelocity);
        pitchTorque += -localAngularVelocity.x * p.pitchDamping * controlAuthority;
        rollTorque += -localAngularVelocity.z * p.rollDamping * controlAuthority;
        yawTorque += -localAngularVelocity.y * p.yawDamping * controlAuthority;

        // Acceleration mode: Unity multiplies by the inertia tensor, so these are angular
        // accelerations and the physical torque scales with the aircraft's inertia automatically.
        _rigidbody.AddRelativeTorque(pitchTorque, yawTorque, rollTorque, ForceMode.Acceleration);
    }

    private void ClampAirspeed()
    {
        float maxAirspeed = ActivePreset.maxAirspeed;
        if (_rigidbody.linearVelocity.sqrMagnitude > maxAirspeed * maxAirspeed)
            _rigidbody.linearVelocity = _rigidbody.linearVelocity.normalized * maxAirspeed;
    }
}
