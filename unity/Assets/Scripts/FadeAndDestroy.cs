using UnityEngine;

// Fades a Light to zero over `duration`, then destroys its GameObject. Split out so
// ExplosionEffect (a static helper, not a MonoBehaviour) can still animate the flash.
public class FadeAndDestroy : MonoBehaviour
{
    private Light _light;
    private float _duration;
    private float _startIntensity;
    private float _elapsed;
    private float _flickerAmplitude;
    private float _flickerFrequency;
    private float _flickerSeed;

    /// <summary>flickerAmplitude > 0 adds Perlin-noise flicker on top of the fade - a fire's afterglow reads as fake with a perfectly smooth falloff.</summary>
    public void Init(Light light, float duration, float flickerAmplitude = 0f, float flickerFrequency = 0f)
    {
        _light = light;
        _duration = duration;
        _startIntensity = light.intensity;
        _flickerAmplitude = flickerAmplitude;
        _flickerFrequency = flickerFrequency;
        _flickerSeed = Random.Range(0f, 100f);
    }

    private void Update()
    {
        _elapsed += Time.deltaTime;
        float t = Mathf.Clamp01(_elapsed / _duration);
        float baseIntensity = Mathf.Lerp(_startIntensity, 0f, t);

        float flicker = _flickerAmplitude > 0f
            ? (Mathf.PerlinNoise(_flickerSeed, _elapsed * _flickerFrequency) - 0.5f) * 2f * _flickerAmplitude * (1f - t)
            : 0f;

        _light.intensity = Mathf.Max(0f, baseIntensity + flicker);
        if (t >= 1f) Destroy(gameObject);
    }
}
