using System;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

// Reads gesture_classifier.ino's on-device classifiers over serial and exposes the same input
// surface as GloveFlightInput/KeyboardFlightInput. Expects
// "<poseLabel>,<poseConfidence>,<throttleLabel>,<throttleConfidence>\n" - poseLabel is one of the 9
// hand-orientation classes (including the diagonal combos) from the hub sensor, throttleLabel is
// neutral/extend/curl from the fingers. Separate models so pose and throttle can be driven at once.
//
// A combo like climb_bank_right is its own trained label, not independent pitch/roll axes - it
// only works if capture_gesture_data.py actually recorded it, and only one pose label comes back
// per sample. Each label maps to a fixed (pitch, roll) target below.
//
// Throttle isn't auto-ramped here like GloveFlightInput - it's driven by the finger gesture:
// "extend" ramps up, "curl" ramps down, "neutral"/low-confidence holds steady.
//
// Opens the device as a raw Unix character file, same reasoning as GloveFlightInput (Unity's
// bundled SerialPort is Windows-only).
[RequireComponent(typeof(AudioSource))]
public class GestureFlightInput : MonoBehaviour, IFlightInput
{
    [Header("Serial Connection")]
    [Tooltip("Device path the glove enumerates as, e.g. /dev/cu.usbmodemXXXX on macOS/Linux (check the Arduino IDE's port dropdown).")]
    [SerializeField] private string devicePath = "/dev/cu.usbmodem111201";

    [Header("Pose Gesture Targets")]
    [Tooltip("Pitch axis target while the pose classifier reports 'climb' (or a climb_bank_* combo), range [-1, 1]. Not full deflection by default - a discrete classifier snapping straight to max input reads as twitchier than continuous stick control.")]
    [SerializeField, Range(0f, 1f)] private float climbPitchTarget = 1.0f;

    [Tooltip("Pitch axis target (negative = nose down) while the pose classifier reports 'dive' (or a dive_bank_* combo).")]
    [SerializeField, Range(0f, 1f)] private float divePitchTarget = 0.8f;

    [Tooltip("Roll axis target while the pose classifier reports 'bank_left'/'bank_right' (or a climb_bank_*/dive_bank_* combo).")]
    [SerializeField, Range(0f, 1f)] private float bankRollTarget = 0.8f;

    [Tooltip("Pose classifications below this confidence are ignored - the last confident target is held instead of snapping to a possibly-wrong low-confidence guess (e.g. a noisy frame mid-transition between two real poses).")]
    [SerializeField, Range(0f, 1f)] private float minPoseConfidence = 0.6f;

    [Header("Stick Smoothing")]
    [Tooltip("Higher values respond to a new pose classification faster; lower values give a softer, more damped transition between poses.")]
    [SerializeField, Range(1f, 30f)] private float smoothingSpeed = 6f;

    [Header("Throttle (Finger Control)")]
    [Tooltip("How much Throttle ramps up/down per second while the throttle classifier holds 'extend'/'curl'. 0.5 = 0-to-full in 2 seconds of holding the pose.")]
    [SerializeField] private float throttleRampSpeed = 0.5f;

    [Tooltip("Throttle classifications below this confidence are ignored - Throttle holds its current value instead of drifting on a noisy guess.")]
    [SerializeField, Range(0f, 1f)] private float minThrottleConfidence = 0.6f;

    [Tooltip("Plays once, right when the throttle classifier switches into 'curl' (slowing down) - not every frame the pose is held. Leave empty to skip.")]
    [SerializeField] private AudioClip slowDownClip;

    /// <summary>Smoothed pitch axis, range [-1, 1].</summary>
    public float PitchInput { get; private set; }

    /// <summary>Smoothed roll axis, range [-1, 1].</summary>
    public float RollInput { get; private set; }

    /// <summary>Always 0 - neither classifier has a yaw pose; AerodynamicFlightController's automatic weathervane stability handles it.</summary>
    public float YawInput { get; private set; }

    /// <summary>Engine throttle, range [0, 1]. Starts at 0 and only changes while the throttle classifier holds 'extend' (increases) or 'curl' (decreases) with enough confidence.</summary>
    public float Throttle { get; private set; }

    private FileStream _readStream;
    private StreamReader _reader;
    private Thread _readThread;
    private volatile bool _running;

    private readonly object _lineLock = new object();
    private string _latestLine;

    // last confident pose classification, as a (pitch, roll) target - smoothed toward in Update()
    private float _targetPitch;
    private float _targetRoll;

    // last confident throttle direction: +1 extend, -1 curl, 0 neutral/unsure
    private float _throttleDirection;

    private AudioSource _audioSource;

    /// <summary>Lets a bootstrapper set the device path before Start() opens it, mirroring GloveFlightInput.Configure. slowDown overrides the Inspector-assigned clip when non-null.</summary>
    public void Configure(string gloveDevicePath, AudioClip slowDown = null)
    {
        devicePath = gloveDevicePath;
        if (slowDown != null) slowDownClip = slowDown;
    }

    private void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        _audioSource.playOnAwake = false;
        _audioSource.spatialBlend = 0f; // a gesture-feedback cue for the player, not a positioned world sound

        try
        {
            _readStream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _reader = new StreamReader(_readStream);

            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GestureFlightInput] Could not open '{devicePath}' ({e.Message}). Flying with neutral input.");
            _readStream = null;
        }
    }

    // Runs on a background thread: StreamReader.ReadLine() blocks until a full line arrives,
    // which would freeze Unity's main thread if called directly from Update().
    private void ReadLoop()
    {
        while (_running)
        {
            string line;
            try
            {
                line = _reader.ReadLine();
            }
            catch (Exception)
            {
                break; // stream closed from OnDestroy - exit quietly.
            }

            if (line == null) break; // device disconnected/closed.

            lock (_lineLock) { _latestLine = line; }
        }
    }

    private void Update()
    {
        string line = null;
        lock (_lineLock)
        {
            if (_latestLine != null) { line = _latestLine; _latestLine = null; }
        }
        if (line != null) ParseLine(line);

        float t = 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);
        PitchInput = Mathf.Lerp(PitchInput, _targetPitch, t);
        RollInput = Mathf.Lerp(RollInput, _targetRoll, t);

        Throttle = Mathf.Clamp01(Throttle + _throttleDirection * throttleRampSpeed * Time.deltaTime);
    }

    private void ParseLine(string line)
    {
        string[] parts = line.Trim().Split(',');
        if (parts.Length != 4) return;

        string poseLabel = parts[0];
        string throttleLabel = parts[2];
        bool havePoseConfidence = float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float poseConfidence);
        bool haveThrottleConfidence = float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float throttleConfidence);
        if (!havePoseConfidence || !haveThrottleConfidence) return;

        if (poseConfidence >= minPoseConfidence)
        {
            switch (poseLabel)
            {
                case "neutral":
                    _targetPitch = 0f;
                    _targetRoll = 0f;
                    break;
                case "climb":
                    _targetPitch = climbPitchTarget;
                    _targetRoll = 0f;
                    break;
                case "dive":
                    _targetPitch = -divePitchTarget;
                    _targetRoll = 0f;
                    break;
                case "bank_left":
                    _targetPitch = 0f;
                    _targetRoll = -bankRollTarget;
                    break;
                case "bank_right":
                    _targetPitch = 0f;
                    _targetRoll = bankRollTarget;
                    break;
                case "climb_bank_left":
                    _targetPitch = climbPitchTarget;
                    _targetRoll = -bankRollTarget;
                    break;
                case "climb_bank_right":
                    _targetPitch = climbPitchTarget;
                    _targetRoll = bankRollTarget;
                    break;
                case "dive_bank_left":
                    _targetPitch = -divePitchTarget;
                    _targetRoll = -bankRollTarget;
                    break;
                case "dive_bank_right":
                    _targetPitch = -divePitchTarget;
                    _targetRoll = bankRollTarget;
                    break;
                default:
                    // Unrecognized label (e.g. a version mismatch between the flashed model and
                    // this script's switch cases) - ignore rather than guess.
                    break;
            }
        }
        // else: hold the last confident pose target instead of jittering.

        if (throttleConfidence >= minThrottleConfidence)
        {
            bool wasCurling = _throttleDirection < 0f;
            switch (throttleLabel)
            {
                case "neutral":
                    _throttleDirection = 0f;
                    break;
                case "extend":
                    _throttleDirection = 1f;
                    break;
                case "curl":
                    _throttleDirection = -1f;
                    break;
                default:
                    break;
            }

            // edge-triggered on entering curl, so it plays once per gesture instead of every line
            // the classifier keeps reporting "curl" while the pose is held.
            if (!wasCurling && _throttleDirection < 0f && slowDownClip != null)
                _audioSource.PlayOneShot(slowDownClip);
        }
        // else: hold the last confident throttle direction instead of jittering.
    }

    private void OnDestroy()
    {
        _running = false;
        try { _readStream?.Close(); } catch { /* already gone */ }
        _readThread?.Join(200);
    }
}
