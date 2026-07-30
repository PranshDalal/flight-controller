using UnityEngine;

// Keyboard input source. Throttle starts at 0 so the aircraft sits idle until you spool it up.
public class KeyboardFlightInput : MonoBehaviour, IFlightInput
{
    [Header("Stick Smoothing")]
    [Tooltip("Higher values respond to key presses faster; lower values give a softer, more damped feel.")]
    [SerializeField, Range(1f, 30f)] private float smoothingSpeed = 8f;

    [Header("Throttle")]
    [Tooltip("How much Throttle changes per second while a throttle key is held. 0.5 = 0-to-full spool-up in 2 seconds.")]
    [SerializeField] private float throttleRampSpeed = 0.5f;

    /// <summary>Smoothed pitch axis, range [-1, 1]. W/Up = nose down, S/Down = nose up.</summary>
    public float PitchInput { get; private set; }

    /// <summary>Smoothed roll axis, range [-1, 1]. A/Left = bank left, D/Right = bank right.</summary>
    public float RollInput { get; private set; }

    /// <summary>Smoothed yaw/rudder axis, range [-1, 1]. Q = yaw left, E = yaw right.</summary>
    public float YawInput { get; private set; }

    /// <summary>Engine throttle, range [0, 1]. Starts at 0. Hold Shift to spool up, Ctrl to throttle back.</summary>
    public float Throttle { get; private set; }

    private void Update()
    {
        // Vertical/Horizontal already cover WASD + arrow keys via Unity's default Input Manager.
        float targetPitch = -Input.GetAxisRaw("Vertical");
        float targetRoll = Input.GetAxisRaw("Horizontal");
        float targetYaw = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);

        float t = 1f - Mathf.Exp(-smoothingSpeed * Time.deltaTime);
        PitchInput = Mathf.Lerp(PitchInput, targetPitch, t);
        RollInput = Mathf.Lerp(RollInput, targetRoll, t);
        YawInput = Mathf.Lerp(YawInput, targetYaw, t);

        bool throttleUp = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool throttleDown = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        if (throttleUp) Throttle += throttleRampSpeed * Time.deltaTime;
        if (throttleDown) Throttle -= throttleRampSpeed * Time.deltaTime;
        Throttle = Mathf.Clamp01(Throttle);
    }
}
