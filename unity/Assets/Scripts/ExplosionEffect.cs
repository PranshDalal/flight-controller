using UnityEngine;

// Explosion VFX built entirely at runtime - no imported assets, same approach WorldBootstrapper
// uses for the rest of the scene. A turbulent fireball, flying sparks, a billowing smoke plume,
// physical debris chunks, a flash plus a lingering flickering fire glow, and a layered
// crack+rumble boom (filtered noise, no audio clip needed), all cleaned up after a few seconds.
public static class ExplosionEffect
{
    public static void Spawn(Vector3 position)
    {
        var root = new GameObject("Explosion");
        root.transform.position = position;

        BuildFire(root.transform);
        BuildSparks(root.transform);
        BuildSmoke(root.transform);
        BuildDebris(root.transform);
        BuildFlash(root.transform);
        BuildBoom(root.transform);

        Object.Destroy(root, 5f);
    }

    private static void BuildFire(Transform parent)
    {
        var go = new GameObject("Fire");
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 16f);
        main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 4f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.3f), new Color(1f, 0.25f, 0.03f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0.15f;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 55) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.5f;

        // Turbulence instead of a clean radial burst - real fire roils and licks unpredictably as
        // it expands, a straight-line sphere burst reads as obviously fake.
        ParticleSystem.NoiseModule noise = ps.noise;
        noise.enabled = true;
        noise.strength = 4f;
        noise.frequency = 0.8f;
        noise.scrollSpeed = 1.5f;
        noise.quality = ParticleSystemNoiseQuality.Medium;

        ParticleSystem.RotationOverLifetimeModule rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-180f, 180f);

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(Color.white, 0f),
                new GradientColorKey(new Color(1f, 0.4f, 0.05f), 0.4f),
                new GradientColorKey(new Color(0.3f, 0.08f, 0f), 1f)
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = grad;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        var sizeCurve = new AnimationCurve(
            new Keyframe(0f, 0.5f),
            new Keyframe(0.25f, 1.5f),
            new Keyframe(1f, 0.8f));
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        go.GetComponent<ParticleSystemRenderer>().material = ParticleMaterial(additive: true);
    }

    // Hot debris flung outward on impact - short-lived, additive, trailed so they read as
    // streaking embers rather than more fire.
    private static void BuildSparks(Transform parent)
    {
        var go = new GameObject("Sparks");
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.loop = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 1f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(14f, 30f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.95f, 0.6f), new Color(1f, 0.55f, 0.1f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 2.5f; // falls hard and fast, like hot metal rather than drifting smoke

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 35) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.3f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.4f, 0.05f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = grad;

        ParticleSystem.TrailModule trails = ps.trails;
        trails.enabled = true;
        trails.ratio = 1f;
        trails.lifetime = 0.15f;
        trails.minVertexDistance = 0.05f;
        trails.widthOverTrail = new ParticleSystem.MinMaxCurve(0.5f);

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        Material mat = ParticleMaterial(additive: true);
        renderer.material = mat;
        renderer.trailMaterial = mat;
    }

    private static void BuildSmoke(Transform parent)
    {
        var go = new GameObject("Smoke");
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.loop = false;
        main.startDelay = 0.2f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(2.8f, 3.8f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 3.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(2f, 4f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.15f, 0.13f, 0.11f), new Color(0.04f, 0.04f, 0.04f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = -0.08f; // drifts up slightly

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 26) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.8f;

        // Same turbulence trick as the fire - keeps the plume billowing instead of expanding as a
        // uniform, obviously-simulated puff.
        ParticleSystem.NoiseModule noise = ps.noise;
        noise.enabled = true;
        noise.strength = 2.5f;
        noise.frequency = 0.4f;
        noise.scrollSpeed = 0.6f;
        noise.quality = ParticleSystemNoiseQuality.Medium;

        ParticleSystem.RotationOverLifetimeModule rotation = ps.rotationOverLifetime;
        rotation.enabled = true;
        rotation.z = new ParticleSystem.MinMaxCurve(-25f, 25f);

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.25f, 0.12f, 0.05f), 0f),
                new GradientColorKey(Color.black, 0.3f),
                new GradientColorKey(new Color(0.3f, 0.3f, 0.3f), 1f)
            },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.85f, 0.5f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = grad;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 3f));

        go.GetComponent<ParticleSystemRenderer>().material = ParticleMaterial(additive: false);
    }

    // Physical fragments flung outward and left to bounce/tumble under real physics - a burst of
    // particles alone reads as a light show, tangible debris sells the impact.
    private static void BuildDebris(Transform parent)
    {
        const int chunkCount = 8;
        var rng = new System.Random();

        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = GameObject.CreatePrimitive(PrimitiveType.Cube);
            chunk.name = "DebrisChunk";
            chunk.transform.SetParent(parent, false);
            float scale = 0.3f + (float)rng.NextDouble() * 0.6f;
            chunk.transform.localScale = new Vector3(scale, scale * 0.6f, scale);
            chunk.transform.rotation = Random.rotation;
            chunk.GetComponent<Renderer>().sharedMaterial = DebrisMaterial();

            var rb = chunk.AddComponent<Rigidbody>();
            rb.mass = 5f;
            Vector3 direction = Random.onUnitSphere;
            direction.y = Mathf.Abs(direction.y) * 1.3f + 0.3f; // biased upward so chunks arc outward instead of burrowing into the ground
            rb.linearVelocity = direction.normalized * (6f + (float)rng.NextDouble() * 10f);
            rb.angularVelocity = Random.insideUnitSphere * 10f;

            Object.Destroy(chunk, 4f);
        }
    }

    private static void BuildFlash(Transform parent)
    {
        var flashGO = new GameObject("Flash");
        flashGO.transform.SetParent(parent, false);
        var flash = flashGO.AddComponent<Light>();
        flash.type = LightType.Point;
        flash.color = new Color(1f, 0.85f, 0.6f);
        flash.intensity = 12f;
        flash.range = 45f;
        flashGO.AddComponent<FadeAndDestroy>().Init(flash, 0.25f);

        // Lingers after the initial flash dies out, like the wreckage is still burning - flickers
        // instead of fading smoothly, since a steady light reads as artificial for an open fire.
        var glowGO = new GameObject("Afterglow");
        glowGO.transform.SetParent(parent, false);
        var glow = glowGO.AddComponent<Light>();
        glow.type = LightType.Point;
        glow.color = new Color(1f, 0.5f, 0.15f);
        glow.intensity = 5f;
        glow.range = 30f;
        glowGO.AddComponent<FadeAndDestroy>().Init(glow, 2.2f, flickerAmplitude: 1.5f, flickerFrequency: 18f);
    }

    private static void BuildBoom(Transform parent)
    {
        var go = new GameObject("Boom");
        go.transform.SetParent(parent, false);
        var source = go.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.clip = ProceduralBoomClip();
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.maxDistance = 400f;
        source.Play();
    }

    private static Material _cachedAdditiveParticleMaterial;
    private static Material _cachedAlphaParticleMaterial;

    // additive (fire/sparks) glows and stacks brighter where particles overlap; alpha-blended
    // (smoke) stays opaque-ish and occludes like a real dark plume should.
    private static Material ParticleMaterial(bool additive)
    {
        if (additive && _cachedAdditiveParticleMaterial != null) return _cachedAdditiveParticleMaterial;
        if (!additive && _cachedAlphaParticleMaterial != null) return _cachedAlphaParticleMaterial;

        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                        ?? Shader.Find("Particles/Standard Unlit")
                        ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader);

        Texture2D tex = SoftParticleTexture();
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);

        if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (mat.HasProperty("_DstBlend"))
        {
            mat.SetFloat("_DstBlend", (float)(additive
                ? UnityEngine.Rendering.BlendMode.One
                : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
        }
        if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        if (additive) _cachedAdditiveParticleMaterial = mat;
        else _cachedAlphaParticleMaterial = mat;
        return mat;
    }

    private static Texture2D _cachedParticleTexture;

    // A soft radial falloff, generated once and cached - a flat Sprites/Default quad renders as a
    // visible hard-edged square, which instantly reads as fake for something like fire or smoke.
    private static Texture2D SoftParticleTexture()
    {
        if (_cachedParticleTexture != null) return _cachedParticleTexture;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        Vector2 center = new Vector2((size - 1) / 2f, (size - 1) / 2f);
        float maxDist = size / 2f;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), center) / maxDist;
                float alpha = Mathf.Clamp01(1f - dist);
                alpha *= alpha; // soft core, sharper falloff toward the rim
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        _cachedParticleTexture = tex;
        return tex;
    }

    private static Material _cachedDebrisMaterial;
    private static Material DebrisMaterial()
    {
        if (_cachedDebrisMaterial != null) return _cachedDebrisMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Diffuse");
        var mat = new Material(shader);
        Color color = new Color(0.08f, 0.07f, 0.06f); // charred, not just generic rock-gray
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        _cachedDebrisMaterial = mat;
        return mat;
    }

    // Two layers: a short bright "crack" (raw noise, fast decay - the sharp impact) under a
    // low-passed "rumble" (smoothed noise, slower decay - the bass thump). Plain white noise alone
    // sounds like static/hiss, not the low "whump" of an actual explosion.
    private static AudioClip ProceduralBoomClip()
    {
        const int sampleRate = 44100;
        const float durationSeconds = 1.6f;
        int sampleCount = (int)(sampleRate * durationSeconds);
        var clip = AudioClip.Create("Boom", sampleCount, 1, sampleRate, false);

        var samples = new float[sampleCount];
        var rng = new System.Random();

        float lowPassState = 0f;
        const float lowPassCoeff = 0.045f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);

            lowPassState += (noise - lowPassState) * lowPassCoeff;
            float rumble = lowPassState * Mathf.Exp(-t * 2.2f);

            float crackEnvelope = Mathf.Exp(-t * 14f);
            float crack = noise * crackEnvelope;

            samples[i] = Mathf.Clamp(crack * 0.6f + rumble * 1.3f, -1f, 1f);
        }
        clip.SetData(samples, 0);
        return clip;
    }
}
