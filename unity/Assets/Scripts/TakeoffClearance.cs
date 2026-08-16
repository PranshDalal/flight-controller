using UnityEngine;

// Plays an ATC "cleared for takeoff" line once, immediately when the aircraft spawns - and again
// on every respawn after a crash. Unlike EngineStartup, this doesn't gate on throttle or loop; it's
// just a one-shot cue.
[RequireComponent(typeof(AudioSource))]
public class TakeoffClearance : MonoBehaviour
{
    private AudioSource _audioSource;
    private AudioClip _clip;

    /// <summary>Wires up the clip and plays it immediately. Pass a null clip to skip the sound.</summary>
    public void Configure(AudioClip clip)
    {
        _audioSource = GetComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 1f;
        _clip = clip;
        Play();
    }

    /// <summary>Replays the clearance line - call on respawn after a crash.</summary>
    public void Play()
    {
        if (_clip == null) return;
        _audioSource.PlayOneShot(_clip);
    }
}
