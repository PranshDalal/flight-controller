using UnityEngine;

// Procedurally builds a minimal open-world flight-sim scene at runtime: a low-poly landscape,
// an active runway, a placeholder airplane wired up with AerodynamicFlightController and
// CrashEffects, an input manager running GestureFlightInput (the glove's on-device gesture
// classifier is the only flight input), a SmoothFlightCamera chasing the plane, and a speed HUD.
//
// Usage: drop this on a single empty GameObject in an empty scene and press Play. No manual
// scene setup required. Swap the placeholder primitive airplane for a real model later by
// replacing BuildAirplane()'s visuals - the Rigidbody/AerodynamicFlightController wiring stays.
[DefaultExecutionOrder(-100)]
public class WorldBootstrapper : MonoBehaviour
{
    [Header("Landscape")]
    [SerializeField] private float terrainSize = 4000f;
    [SerializeField] private int scatterPropCount = 250;
    [SerializeField] private float scatterRadius = 1800f;

    [Header("Runway")]
    [SerializeField] private float runwayLength = 1500f;
    [SerializeField] private float runwayWidth = 45f;

    [Header("Input Source")]
    [Tooltip("Device path the glove's Arduino Nano 33 BLE Sense enumerates as (check the Arduino IDE's port dropdown), e.g. /dev/cu.usbmodem111201 on macOS.")]
    [SerializeField] private string gloveDevicePath = "/dev/cu.usbmodem111201";

    [Header("Airplane")]
    [Tooltip("Optional: drag an imported model (or its prefab) here to replace the placeholder capsule/cube aircraft. Its nose must point down local +Z (the blue axis in the Scene view) - wrap it in an empty parent and rotate that if the model itself faces a different way. Leave empty to keep the placeholder.")]
    [SerializeField] private GameObject aircraftModelPrefab;

    [Tooltip("Plays once the player asks for throttle (and again after respawning from a crash), then loops its last few seconds to keep the engine running. Thrust stays at zero until it finishes spooling up, so the plane can't start rolling before the engine sounds ready. Leave empty to skip the sound and allow thrust immediately.")]
    [SerializeField] private AudioClip engineStartupClip;

    [Tooltip("Plays once immediately when the plane spawns (and again on every respawn after a crash) - an ATC clearance line, independent of engine startup. Leave empty to skip.")]
    [SerializeField] private AudioClip clearForTakeoffClip;

    private readonly System.Random _rng = new System.Random(1337);

    private void Awake()
    {
        BuildLighting();
        Transform runwayStart = BuildLandscapeAndRunway();
        IFlightInput flightInput = BuildInputManager();
        Transform plane = BuildAirplane(runwayStart, flightInput);
        BuildCamera(plane);
        BuildHud(plane.GetComponent<Rigidbody>());
    }

    private void BuildLighting()
    {
        if (FindObjectOfType<Light>() != null) return;

        var sunGO = new GameObject("Sun");
        var sun = sunGO.AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.95f, 0.85f);
        sun.intensity = 1.2f;
        sunGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        RenderSettings.sun = sun;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.53f, 0.7f, 0.95f);
        RenderSettings.ambientGroundColor = new Color(0.3f, 0.3f, 0.25f);
    }

    /// <returns>Transform positioned at the near end of the runway centerline, facing down its length.</returns>
    private Transform BuildLandscapeAndRunway()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Landscape";
        ground.transform.localScale = Vector3.one * (terrainSize / 10f);
        ground.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.35f, 0.55f, 0.25f));

        var runwayRoot = new GameObject("Runway").transform;

        var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
        strip.name = "RunwayStrip";
        strip.transform.SetParent(runwayRoot, false);
        strip.transform.localPosition = new Vector3(0f, 0.05f, 0f);
        strip.transform.localScale = new Vector3(runwayWidth, 0.1f, runwayLength);
        strip.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.12f, 0.12f, 0.13f));

        // Centerline markings.
        const int stripeCount = 24;
        float stripeSpacing = runwayLength / stripeCount;
        for (int i = 0; i < stripeCount; i++)
        {
            var stripe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stripe.name = "CenterlineStripe";
            stripe.transform.SetParent(runwayRoot, false);
            float z = -runwayLength / 2f + stripeSpacing * (i + 0.5f);
            stripe.transform.localPosition = new Vector3(0f, 0.11f, z);
            stripe.transform.localScale = new Vector3(1.5f, 0.05f, stripeSpacing * 0.5f);
            stripe.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(Color.white);
            Destroy(stripe.GetComponent<Collider>());
        }

        ScatterLowPolyProps(runwayRoot);

        var runwayStart = new GameObject("RunwayStart").transform;
        runwayStart.SetParent(runwayRoot, false);
        runwayStart.localPosition = new Vector3(0f, 1.5f, -runwayLength / 2f + 25f);
        runwayStart.localRotation = Quaternion.identity; // faces +Z, down the runway.

        return runwayStart;
    }

    private void ScatterLowPolyProps(Transform parent)
    {
        var propsRoot = new GameObject("Props").transform;
        propsRoot.SetParent(parent, false);

        float halfLength = runwayLength / 2f + 20f;
        float halfWidth = runwayWidth / 2f + 20f;

        for (int i = 0; i < scatterPropCount; i++)
        {
            Vector3 pos = RandomPointAvoidingRunway(halfLength, halfWidth);
            bool isTree = _rng.NextDouble() > 0.35;
            GameObject prop = isTree ? BuildTree() : BuildRock();
            prop.transform.SetParent(propsRoot, false);
            prop.transform.position = pos;
            prop.transform.rotation = Quaternion.Euler(0f, (float)_rng.NextDouble() * 360f, 0f);
        }
    }

    private Vector3 RandomPointAvoidingRunway(float runwayHalfLength, float runwayHalfWidth)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float angle = (float)_rng.NextDouble() * Mathf.PI * 2f;
            float radius = Mathf.Sqrt((float)_rng.NextDouble()) * scatterRadius;
            var point = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

            bool onRunway = Mathf.Abs(point.x) < runwayHalfWidth && Mathf.Abs(point.z) < runwayHalfLength;
            if (!onRunway) return point;
        }
        return new Vector3(scatterRadius, 0f, scatterRadius); // fallback, statistically almost never hit.
    }

    private GameObject BuildTree()
    {
        var root = new GameObject("Tree");

        var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        trunk.transform.SetParent(root.transform, false);
        trunk.transform.localScale = new Vector3(1f, 3f, 1f);
        trunk.transform.localPosition = new Vector3(0f, 3f, 0f);
        trunk.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.4f, 0.26f, 0.13f));
        Destroy(trunk.GetComponent<Collider>());

        var canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        canopy.transform.SetParent(root.transform, false);
        canopy.transform.localScale = new Vector3(4f, 3f, 4f);
        canopy.transform.localPosition = new Vector3(0f, 7f, 0f);
        canopy.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.18f, 0.45f, 0.2f));
        Destroy(canopy.GetComponent<Collider>());

        return root;
    }

    private GameObject BuildRock()
    {
        var rock = GameObject.CreatePrimitive(PrimitiveType.Cube);
        rock.name = "Rock";
        float scale = 2f + (float)_rng.NextDouble() * 6f;
        rock.transform.localScale = new Vector3(scale, scale * 0.7f, scale);
        rock.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.45f, 0.44f, 0.42f));
        Destroy(rock.GetComponent<Collider>());
        return rock;
    }

    private IFlightInput BuildInputManager()
    {
        var inputManager = new GameObject("Input Manager");
        var gesture = inputManager.AddComponent<GestureFlightInput>();
        gesture.Configure(gloveDevicePath);
        return gesture;
    }

    private Transform BuildAirplane(Transform spawnPoint, IFlightInput flightInput)
    {
        var planeRoot = new GameObject("Airplane");
        planeRoot.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);

        Bounds? modelBounds = null;

        if (aircraftModelPrefab != null)
        {
            var model = Instantiate(aircraftModelPrefab, planeRoot.transform);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;

            // imported models rarely pivot at the landing gear - shift so the lowest point sits at Y=0
            Bounds bounds = CalculateLocalBounds(model, planeRoot.transform);
            model.transform.localPosition -= new Vector3(0f, bounds.min.y, 0f);
            bounds.center -= new Vector3(0f, bounds.min.y, 0f);
            modelBounds = bounds;
        }
        else
        {
            var fuselage = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            fuselage.name = "Fuselage";
            fuselage.transform.SetParent(planeRoot.transform, false);
            fuselage.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // align capsule's long axis with local forward (Z).
            fuselage.transform.localScale = new Vector3(1.5f, 4f, 1.5f);
            fuselage.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.8f, 0.8f, 0.85f));
            Destroy(fuselage.GetComponent<Collider>());

            var wings = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wings.name = "Wings";
            wings.transform.SetParent(planeRoot.transform, false);
            wings.transform.localScale = new Vector3(9f, 0.2f, 1.6f);
            wings.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.7f, 0.1f, 0.1f));
            Destroy(wings.GetComponent<Collider>());

            var tailFin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            tailFin.name = "TailFin";
            tailFin.transform.SetParent(planeRoot.transform, false);
            tailFin.transform.localScale = new Vector3(0.2f, 1.4f, 1f);
            tailFin.transform.localPosition = new Vector3(0f, 0.9f, -3.4f);
            tailFin.GetComponent<Renderer>().sharedMaterial = CreateColorMaterial(new Color(0.7f, 0.1f, 0.1f));
            Destroy(tailFin.GetComponent<Collider>());
        }

        // one collider for the whole aircraft, fit to the real model's geometry when one's in use
        var bodyCollider = planeRoot.AddComponent<BoxCollider>();
        if (modelBounds.HasValue)
        {
            // force X centered even on a slightly asymmetric mesh - an offset here skews the
            // auto center of mass and leaves the aircraft resting with a persistent yaw
            Vector3 center = modelBounds.Value.center;
            bodyCollider.center = new Vector3(0f, center.y, center.z);
            bodyCollider.size = modelBounds.Value.size;
        }
        else
        {
            bodyCollider.center = new Vector3(0f, 0.3f, 0f);
            bodyCollider.size = new Vector3(9f, 2f, 8f);
        }

        // real tires have low rolling resistance - Unity's default friction would eat most of the
        // engine's thrust just fighting the ground
        bodyCollider.sharedMaterial = new PhysicsMaterial("LowRollingResistance")
        {
            dynamicFriction = 0.02f,
            staticFriction = 0.05f,
            frictionCombine = PhysicsMaterialCombine.Minimum
        };

        var rb = planeRoot.AddComponent<Rigidbody>();
        rb.mass = 1000f;

        // added before AerodynamicFlightController so its GetComponent<EngineStartup>() in Awake() finds it
        var engineStartup = planeRoot.AddComponent<EngineStartup>();
        engineStartup.Configure(engineStartupClip, flightInput);

        // added before CrashEffects so its GetComponent<TakeoffClearance>() in Awake() finds it
        var takeoffClearance = planeRoot.AddComponent<TakeoffClearance>();
        takeoffClearance.Configure(clearForTakeoffClip);

        var flightController = planeRoot.AddComponent<AerodynamicFlightController>();
        flightController.SetFlightInput(flightInput);

        // added last so its Awake() (which grabs the renderers/controller above via
        // GetComponent) runs after everything it needs to find already exists
        planeRoot.AddComponent<CrashEffects>();

        return planeRoot.transform;
    }

    // combined bounds of every renderer under `model`, in `relativeTo`'s local space - re-measured
    // corner by corner since a rotated AABB doesn't just translate onto another AABB
    private static Bounds CalculateLocalBounds(GameObject model, Transform relativeTo)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(Vector3.zero, Vector3.zero);

        Bounds localBounds = new Bounds(relativeTo.InverseTransformPoint(renderers[0].bounds.center), Vector3.zero);
        foreach (Renderer renderer in renderers)
        {
            Vector3 center = renderer.bounds.center;
            Vector3 extents = renderer.bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = center + new Vector3(
                    (i & 1) == 0 ? -extents.x : extents.x,
                    (i & 2) == 0 ? -extents.y : extents.y,
                    (i & 4) == 0 ? -extents.z : extents.z);
                localBounds.Encapsulate(relativeTo.InverseTransformPoint(corner));
            }
        }
        return localBounds;
    }

    private void BuildCamera(Transform plane)
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            var camGO = new GameObject("Main Camera") { tag = "MainCamera" };
            cam = camGO.AddComponent<Camera>();
            camGO.AddComponent<AudioListener>();
        }

        var chaseCam = cam.GetComponent<SmoothFlightCamera>();
        if (chaseCam == null)
            chaseCam = cam.gameObject.AddComponent<SmoothFlightCamera>();

        chaseCam.SetTarget(plane);
        chaseCam.SnapToTarget();
    }

    private void BuildHud(Rigidbody aircraftRigidbody)
    {
        var hudGO = new GameObject("HUD");
        hudGO.AddComponent<FlightHud>().Initialize(aircraftRigidbody);
    }

    private static Material CreateColorMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Diffuse");
        var mat = new Material(shader);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        return mat;
    }
}
