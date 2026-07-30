using System;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

// Reads the glove over USB serial and exposes the same input surface as KeyboardFlightInput.
// Expects "<pitchAxis>,<rollAxis>" per line from flight_controller.ino. No manual throttle/yaw -
// Throttle auto-ramps to cruise and YawInput stays 0 (AerodynamicFlightController's weathervane
// stability handles turns from bank alone).
//
// Opens the device as a raw Unix character file (e.g. /dev/cu.usbmodemXXXX) instead of using
// System.IO.Ports - Unity 6's bundled SerialPort is Windows-only. So devicePath needs to be a
// Unix path, not a COM name.
public class GloveFlightInput : MonoBehaviour, IFlightInput
{
    [Header("Serial Connection")]
    [Tooltip("Device path the glove enumerates as, e.g. /dev/cu.usbmodemXXXX on macOS/Linux (check the Arduino IDE's port dropdown).")]
    [SerializeField] private string devicePath = "/dev/cu.usbmodem111201";

    [Header("Stick Smoothing")]
    [Tooltip("Higher values respond to finger/wrist movement faster; lower values give a softer, more damped feel.")]
    [SerializeField, Range(1f, 30f)] private float smoothingSpeed = 8f;

    [Header("Throttle")]
    [Tooltip("How much Throttle ramps up per second after spawn. 0.5 = 0-to-full spool-up in 2 seconds.")]
    [SerializeField] private float throttleRampSpeed = 0.5f;

    [Header("Calibration")]
    [Tooltip("Press to ask the glove to re-zero its neutral pose (hold hand flat, fingers straight, then press) - useful if the glove has shifted on your hand mid-session.")]
    [SerializeField] private KeyCode recalibrateKey = KeyCode.C;

    /// <summary>Smoothed pitch axis, range [-1, 1]. Tilt hand forward = dive, back = climb.</summary>
    public float PitchInput { get; private set; }

    /// <summary>Smoothed roll axis, range [-1, 1]. Tilt/bank hand left/right = roll.</summary>
    public float RollInput { get; private set; }

    /// <summary>Always 0 - no manual yaw axis; AerodynamicFlightController's automatic weathervane stability handles it.</summary>
    public float YawInput { get; private set; }

    /// <summary>Engine throttle, range [0, 1]. Auto-ramps from 0 to full over throttleRampSpeed right after spawn - no manual control.</summary>
    public float Throttle { get; private set; }

    private FileStream _readStream;
    private FileStream _writeStream;
    private StreamReader _reader;
    private Thread _readThread;
    private volatile bool _running;

    private readonly object _lineLock = new object();
    private string _latestLine;

    // most recent parsed axis values, smoothed toward every frame in Update()
    private float _rawPitchAxis;
    private float _rawRollAxis;

    /// <summary>Lets a bootstrapper set the device path before Start() opens it, mirroring AerodynamicFlightController.SetFlightInput.</summary>
    public void Configure(string gloveDevicePath)
    {
        devicePath = gloveDevicePath;
    }

    private void Start()
    {
        try
        {
            // separate read/write handles so the background read loop and the occasional
            // recalibrate write from the main thread don't have to coordinate
            _readStream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _reader = new StreamReader(_readStream);
            _writeStream = new FileStream(devicePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            _running = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GloveFlightInput] Could not open '{devicePath}' ({e.Message}). Flying with neutral input.");
            _readStream = null;
            _writeStream = null;
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

        if (_writeStream != null && Input.GetKeyDown(recalibrateKey))
        {
            try { _writeStream.WriteByte((byte)'c'); _writeStream.Flush(); }
            catch (Exception e) { Debug.LogWarning($"[GloveFlightInput] Recalibrate write failed: {e.Message}"); }
        }

        float t = 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);
        PitchInput = Mathf.Lerp(PitchInput, _rawPitchAxis, t);
        RollInput = Mathf.Lerp(RollInput, _rawRollAxis, t);

        // No manual throttle input anymore - just spool up to full and stay there.
        Throttle = Mathf.Clamp01(Throttle + throttleRampSpeed * Time.deltaTime);
    }

    private void ParseLine(string line)
    {
        string[] parts = line.Trim().Split(',');
        if (parts.Length != 2) return;

        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float pitchAxis))
            _rawPitchAxis = pitchAxis;
        if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float rollAxis))
            _rawRollAxis = rollAxis;
    }

    private void OnDestroy()
    {
        _running = false;
        try { _readStream?.Close(); } catch { /* already gone */ }
        try { _writeStream?.Close(); } catch { /* already gone */ }
        _readThread?.Join(200);
    }
}
