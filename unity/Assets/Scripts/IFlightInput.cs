// Common surface every input source (keyboard, glove, ...) exposes to AerodynamicFlightController.
public interface IFlightInput
{
    /// <summary>Smoothed pitch axis, range [-1, 1].</summary>
    float PitchInput { get; }

    /// <summary>Smoothed roll axis, range [-1, 1].</summary>
    float RollInput { get; }

    /// <summary>Smoothed yaw/rudder axis, range [-1, 1].</summary>
    float YawInput { get; }

    /// <summary>Engine throttle, range [0, 1].</summary>
    float Throttle { get; }
}
