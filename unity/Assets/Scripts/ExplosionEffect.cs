using UnityEngine;

// Explosion VFX built entirely at runtime - no imported assets, same approach WorldBootstrapper
// uses for the rest of the scene. A fire burst, a drifting smoke puff, a quick light flash, and a
// procedural boom (filtered noise burst, no audio clip needed), all cleaned up after a few seconds.
public static class ExplosionEffect
{
    public static void Spawn(Vector3 position)
    {
        var root = new GameObject("Explosion");
        root.transform.position = position;

        BuildFire(root.transform);
        BuildSmoke(root.transform);
        BuildFlash(root.transform);
        BuildBoom(root.transform);

        Object.Destroy(root, 4f);
    }

    private static void BuildFire(Transform parent)
    {
        var go = new GameObject("Fire");
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.loop = false;
        main.startLifetime = 0.6f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(8f, 18f);
        main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.75f, 0.2f), new Color(1f, 0.3f, 0.05f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0.3f;

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 40) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.6f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.4f, 0.1f, 0f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = grad;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 1.6f));

        go.GetComponent<ParticleSystemRenderer>().material = ParticleMaterial();
    }

    private static void BuildSmoke(Transform parent)
    {
        var go = new GameObject("Smoke");
        go.transform.SetParent(parent, false);
        var ps = go.AddComponent<ParticleSystem>();

        ParticleSystem.MainModule main = ps.main;
        main.loop = false;
        main.startDelay = 0.15f;
        main.startLifetime = 2.5f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 4f);
        main.startSize = new ParticleSystem.MinMaxCurve(3f, 6f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.2f, 0.2f, 0.2f), new Color(0.05f, 0.05f, 0.05f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = -0.05f; // drifts up slightly

        ParticleSystem.EmissionModule emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 20) });

        ParticleSystem.ShapeModule shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 1f;

        ParticleSystem.ColorOverLifetimeModule colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Color.gray, 1f) },
            new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = grad;

        go.GetComponent<ParticleSystemRenderer>().material = ParticleMaterial();
    }

    private static void BuildFlash(Transform parent)
    {
        var go = new GameObject("Flash");
        go.transform.SetParent(parent, false);
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.7f, 0.3f);
        light.intensity = 8f;
        light.range = 40f;

        go.AddComponent<FadeAndDestroy>().Init(light, 0.35f);
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

    private static Material _cachedParticleMaterial;
    private static Material ParticleMaterial()
    {
        if (_cachedParticleMaterial != null) return _cachedParticleMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                        ?? Shader.Find("Particles/Standard Unlit")
                        ?? Shader.Find("Sprites/Default");
        _cachedParticleMaterial = new Material(shader);
        return _cachedParticleMaterial;
    }

    // White noise with an exponential decay envelope - a cheap boom with no audio asset needed.
    private static AudioClip ProceduralBoomClip()
    {
        const int sampleRate = 44100;
        const float durationSeconds = 0.8f;
        int sampleCount = (int)(sampleRate * durationSeconds);
        var clip = AudioClip.Create("Boom", sampleCount, 1, sampleRate, false);

        var samples = new float[sampleCount];
        var rng = new System.Random();
        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            float envelope = Mathf.Exp(-t * 6f);
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            samples[i] = noise * envelope;
        }
        clip.SetData(samples, 0);
        return clip;
    }
}
