using UnityEngine;

// Waits for the player to actually ask for thrust (Throttle past throttleArmThreshold) before
// doing anything - it doesn't play the instant the plane spawns, only once the plane is actually
// about to start speeding up. From there it plays the jet engine startup sound once and gates
// thrust behind it: AerodynamicFlightController reads Readiness and scales thrust by it, so the
// aircraft physically can't move until the engine sounds like it's actually spooled up. Readiness
// ramps over a fraction of the clip's own length, so a hard cue like "the plane starts rolling"
// always lands after the startup sound - and stays in sync automatically even if the clip gets
// swapped for a different-length one later.
//
// Once the clip finishes, it keeps the engine "running" by looping just its last few seconds. That
// tail is extracted into its own small uncompressed clip up front and looped natively
// (AudioSource.loop always restarts from sample 0) - re-seeking into the original MP3 instead
// doesn't work reliably, since MP3 doesn't support precise arbitrary-position seeking in Unity.
//
// The true end of the clip rarely flows smoothly into the point loopTailSeconds before it, so the
// wrap-around point itself is crossfaded during extraction: the last crossfadeSeconds of the tail
// are blended into its first crossfadeSeconds. That moves the actual seam to right where the
// crossfade region starts, which - since it's made of genuinely adjacent original samples - loops
// cleanly, instead of the seam sitting on an arbitrary, likely-discontinuous cut point.
[RequireComponent(typeof(AudioSource))]
public class EngineStartup : MonoBehaviour
{
    [Tooltip("Throttle input above this counts as the player asking for thrust - the startup sound and thrust gating don't begin until then.")]
    [SerializeField] private float throttleArmThreshold = 0.02f;

    [Tooltip("Fraction of the startup clip's length the engine takes to reach full thrust authority (1 = the whole clip).")]
    [SerializeField, Range(0.1f, 1f)] private float spoolFraction = 0.85f;

    [Tooltip("Once the startup clip finishes, keep looping just its last N seconds instead of going silent.")]
    [SerializeField] private float loopTailSeconds = 4f;

    [Tooltip("Length of the crossfade blended into the loop point to smooth the seam. Shortens the effective loop by this much.")]
    [SerializeField] private float crossfadeSeconds = 0.35f;

    private AudioSource _audioSource;
    private AudioClip _startupClip;
    private AudioClip _tailLoopClip;
    private IFlightInput _flightInput;
    private float _spoolDuration;
    private float _elapsed;
    private bool _hasStarted;
    private bool _tailLoopStarted;

    /// <summary>0 = no thrust available yet, 1 = fully spooled - full thrust authority.</summary>
    public float Readiness { get; private set; }

    /// <summary>Wires up the clip and the input source to watch for throttle. Pass a null clip to skip the sound and never gate thrust.</summary>
    public void Configure(AudioClip startupClip, IFlightInput flightInput)
    {
        _audioSource = GetComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 1f;
        _startupClip = startupClip;
        _flightInput = flightInput;

        if (_startupClip == null)
        {
            Readiness = 1f;
            enabled = false;
            return;
        }

        _spoolDuration = _startupClip.length * spoolFraction;
        _tailLoopClip = ExtractTailClip(_startupClip, loopTailSeconds, crossfadeSeconds);
        ArmForNextThrottle();
    }

    /// <summary>Re-arms the engine to wait for throttle again and re-gates thrust - call on respawn after a crash.</summary>
    public void Replay()
    {
        if (_startupClip == null) return;
        _audioSource.Stop();
        ArmForNextThrottle();
    }

    private void ArmForNextThrottle()
    {
        _hasStarted = false;
        _tailLoopStarted = false;
        _elapsed = 0f;
        Readiness = 0f;
        enabled = true;
    }

    private void Update()
    {
        if (!_hasStarted)
        {
            // no input source to gate on - just start rather than blocking thrust forever
            float throttle = _flightInput != null ? _flightInput.Throttle : 1f;
            if (throttle < throttleArmThreshold) return;

            _hasStarted = true;
            _audioSource.loop = false;
            _audioSource.clip = _startupClip;
            _audioSource.Play();
        }

        if (Readiness < 1f)
        {
            _elapsed += Time.deltaTime;
            Readiness = _spoolDuration > 0f ? Mathf.Clamp01(_elapsed / _spoolDuration) : 1f;
        }

        if (!_tailLoopStarted && !_audioSource.isPlaying)
        {
            _tailLoopStarted = true;
            _audioSource.clip = _tailLoopClip;
            _audioSource.loop = true;
            _audioSource.Play();
        }
    }

    private static AudioClip ExtractTailClip(AudioClip source, float seconds, float crossfadeSeconds)
    {
        int channels = source.channels;
        int frequency = source.frequency;
        int tailFrames = Mathf.Min(source.samples, Mathf.CeilToInt(seconds * frequency));
        int startFrame = source.samples - tailFrames;

        var tail = new float[tailFrames * channels];
        source.GetData(tail, startFrame);

        int crossfadeFrames = Mathf.Clamp(Mathf.CeilToInt(crossfadeSeconds * frequency), 0, tailFrames / 2);
        int outFrames = tailFrames - crossfadeFrames;
        var output = new float[outFrames * channels];

        // blend region: output[0] starts as pure "tail end" content and morphs into pure
        // "tail start" content by output[crossfadeFrames - 1]
        for (int i = 0; i < crossfadeFrames; i++)
        {
            float t = (float)i / crossfadeFrames;
            for (int c = 0; c < channels; c++)
            {
                float startSample = tail[i * channels + c];
                float endSample = tail[(tailFrames - crossfadeFrames + i) * channels + c];
                output[i * channels + c] = Mathf.Lerp(endSample, startSample, t);
            }
        }
        for (int i = crossfadeFrames; i < outFrames; i++)
            for (int c = 0; c < channels; c++)
                output[i * channels + c] = tail[i * channels + c];

        var clip = AudioClip.Create("EngineIdleLoop", outFrames, channels, frequency, false);
        clip.SetData(output, 0);
        return clip;
    }
}
