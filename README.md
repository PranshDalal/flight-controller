# Flight Controller Glove

A wearable, IMU-based glove that flies a plane in a Unity flight sim. Hold a hand pose
(neutral/climb/dive/bank left/bank right, or a climb+bank/dive+bank combo) to control
pitch/roll, and extend or curl your fingers to control throttle.

The project is an Arduino glove firmware that runs two on-device TensorFlow Lite Micro
classifiers, a Unity flight sim that reads their output over USB serial, and a small
toolchain for capturing motion data and training those classifiers.

## How it works

```
Glove (Arduino Nano 33 BLE Sense + TCA9548A mux + 3x MPU-6050 IMU: hub, index, middle)
        |  USB serial: "<pose>,<poseConf>,<throttle>,<throttleConf>"
        v
Unity (GestureFlightInput) -> AerodynamicFlightController -> flight sim
```

`gesture_classifier.ino` runs two on-device TFLite Micro models against a rolling window
of raw IMU samples:

- **Pose** (hub sensor) - `neutral`/`climb`/`dive`/`bank_left`/`bank_right`, plus the four
  diagonal combos (`climb_bank_left`, `climb_bank_right`, `dive_bank_left`,
  `dive_bank_right`). Each label maps to a fixed pitch/roll target in
  `GestureFlightInput.cs`.
- **Throttle** (index/middle finger sensors) - `neutral`/`extend`/`curl`. `extend` ramps
  throttle up, `curl` ramps it down.

Both run independently (you can hold a bank and extend your fingers at once), and are
streamed together as one CSV line. Yaw is fully automatic -
`AerodynamicFlightController`'s aerodynamic weathervane stability handles turns from bank
alone, so there's no yaw pose to hold.

## Repo layout

```
glove/
  src/
    gesture_classifier/      On-device TFLite Micro classifier firmware
      gesture_model.h        Generated - pose classifier weights
      throttle_model.h       Generated - throttle classifier weights
  testing/
    mpu.ino                  Raw IMU reader, one sensor per channel - bring-up/wiring check
    multiplexer.ino          I2C bus scanner for the TCA9548A mux
    gesture_capture/
      gesture_capture.ino    Streams raw, unlabeled IMU samples for data collection
    capture_gesture_data.py  Records labeled training data from gesture_capture.ino
    train_gesture_model.py   Trains gesture_model.h / throttle_model.h from the CSV
    gesture_data.csv         Accumulated labeled training data
unity/
  Assets/Scripts/
    IFlightInput.cs                 Common input interface (pitch/roll/yaw/throttle)
    GestureFlightInput.cs            Reads gesture_classifier.ino over serial
    AerodynamicFlightController.cs   Lift/drag/stall flight model driven by IFlightInput
    WorldBootstrapper.cs             Builds the runtime scene (terrain, runway, plane, camera, HUD)
    EngineStartup.cs, TakeoffClearance.cs, CrashEffects.cs, ExplosionEffect.cs
                                      Engine spool-up gating, ATC callout, crash/explosion FX
    SmoothFlightCamera.cs, FlightHud.cs
                                      Chase camera, airspeed readout
```

## Hardware

- Arduino Nano 33 BLE Sense
- TCA9548A I2C multiplexer
- 3x MPU-6050 IMU: one on the back of the hand (hub), one on the index finger, one on the
  middle finger
- USB cable to the host machine running Unity

Wire everything through the multiplexer, then verify each device is visible before
flashing real firmware:

```bash
# I2C bus scan - confirms the mux and IMUs answer
open glove/testing/multiplexer.ino in Arduino IDE and flash it

# Per-channel raw reads - confirms each IMU is on the channel you expect
open glove/testing/mpu.ino in Arduino IDE and flash it
```

## Getting started

### 1. Flash the glove

Flash `glove/src/gesture_classifier/gesture_classifier.ino` with the Arduino IDE. It
needs the
[tflite-micro-arduino-examples](https://github.com/tensorflow/tflite-micro-arduino-examples)
library cloned into `~/Documents/Arduino/libraries/Arduino_TensorFlowLite` - the
deprecated `Arduino_TensorFlowLite` Library Manager package won't work.

### 2. Open the Unity project

Open `unity/` in Unity, then `Assets/Scenes/FlightSim.unity`. `WorldBootstrapper` builds
the whole scene at runtime, so no manual scene setup is needed - press Play. Set the
glove's serial device path (e.g. `/dev/cu.usbmodemXXXX` on macOS) on the `Input Source`
group's `Glove Device Path` field.

Only one process can hold the glove's serial port at a time - close any Python tooling
before pressing Play in Unity, and vice versa.

### 3. Capture data and train

```bash
cd glove/testing
pip install pyserial

# 1. Flash gesture_capture/gesture_capture.ino (not gesture_classifier.ino)
# 2. Record labeled reps for both the pose and throttle label groups
python3 capture_gesture_data.py --port /dev/tty.usbmodemXXXX

# 3. Train both models from the accumulated CSV
python3 train_gesture_model.py --data gesture_data.csv --group pose
python3 train_gesture_model.py --data gesture_data.csv --group throttle

# 4. Copy the generated gesture_model.h / throttle_model.h into
#    glove/src/gesture_classifier/, then reflash gesture_classifier.ino
```

Run either script with `-h` for full option details.

## Notes

- IMU readings are raw/uncalibrated - remounting the glove differently means
  recapturing and retraining.
- The firmware streams at ~65-66 Hz over USB serial at 115200 baud.

---

## AI disclosure

AI assistance (Claude) was used in this project for documentation and debugging
purposes.
