using UnityEngine;

// Fades a Light to zero over `duration`, then destroys its GameObject. Split out so
// ExplosionEffect (a static helper, not a MonoBehaviour) can still animate the flash.
public class FadeAndDestroy : MonoBehaviour
{
    private Light _light;
    private float _duration;
    private float _startIntensity;
    private float _elapsed;

    public void Init(Light light, float duration)
    {
        _light = light;
        _duration = duration;
        _startIntensity = light.intensity;
    }

    private void Update()
    {
        _elapsed += Time.deltaTime;
        float t = Mathf.Clamp01(_elapsed / _duration);
        _light.intensity = Mathf.Lerp(_startIntensity, 0f, t);
        if (t >= 1f) Destroy(gameObject);
    }
}
