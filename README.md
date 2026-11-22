# Flight Controller Glove

A wearable, IMU-based glove that flies a Gulfstream G650ER in a Unity flight simulator. Hold a hand pose to pitch and roll the plane, and extend or curl your fingers to control throttle. The glove recognizes your gestures on-device with two TensorFlow Lite Micro classifiers and streams the result to Unity over USB serial.

## Features

- **Gesture flying.** Nine hand poses (neutral, climb, dive, bank left/right, and the four climb/dive + bank combos) map to pitch and roll targets.
- **Finger throttle.** Extending your fingers ramps the throttle up, and curling them ramps it down. This works independently of the hand pose, so you can bank and throttle at the same time.
- **On-device ML.** Two TFLite Micro models run on the Arduino against a rolling window of raw IMU samples.
- **Physics-based flight model.** Lift and drag come from angle of attack and airspeed (`L = q·S·C_L`, `D = q·S·C_D`) using G650ER data: a finite-wing lift curve with a smooth stall, induced and compressibility drag, real mass, wing area and thrust, engine spool-up, and the 1976 US Standard Atmosphere.
- **Turns that emerge from the physics.** Lift acts perpendicular to the airflow in the plane's own frame, so banking tilts the lift vector into a turn. Weathervane stability lines the nose up with the turn, so there's no yaw gesture to hold.
- **Simulator extras.** Engine-start sequence, an ATC "cleared for takeoff" call, crash explosions, an endless landscape, a chase camera and an airspeed HUD.
- **Data capture and training toolchain.** Python scripts to record labeled gestures and train both models.

## How it works

```
Glove (Arduino Nano 33 BLE Sense + TCA9548A mux + 3x MPU-6050 IMU: hub, index, middle)
        |  USB serial, 115200 baud, ~66 Hz: "<pose>,<poseConf>,<throttle>,<throttleConf>"
        v
Unity (GestureFlightInput) -> AerodynamicFlightController -> flight sim
```

- **Pose model** (hub sensor on the back of the hand): classifies the nine poses. Each pose maps to a fixed pitch/roll target in `GestureFlightInput.cs`. Predictions below 0.6 confidence are ignored.
- **Throttle model** (index and middle finger sensors): `neutral` / `extend` / `curl`. `extend` ramps throttle up and `curl` ramps it down.
- **Flight model** (`AerodynamicFlightController.cs`): every physics step it computes angle of attack, then lift and drag from the `AircraftModel.G650ER()` parameters, plus thrust, stability and damping torques. Unity's rigidbody physics integrates the forces.

## Hardware

- Arduino Nano 33 BLE Sense
- TCA9548A I2C multiplexer
- 3x MPU-6050 IMU: one on the back of the hand (hub), one on the index finger, one on the middle finger
- USB cable to the computer running Unity

## Repository layout

```
glove/
  src/
    gesture_classifier/      On-device TFLite Micro classifier firmware
      gesture_model.h        Generated: pose classifier weights
      throttle_model.h       Generated: throttle classifier weights
  testing/
    mpu.ino                  Raw IMU reader, one sensor per channel (wiring check)
    multiplexer.ino          I2C bus scanner for the TCA9548A mux
    gesture_capture/
      gesture_capture.ino    Streams raw, unlabeled IMU samples for data collection
    capture_gesture_data.py  Records labeled training data from gesture_capture.ino
    train_gesture_model.py   Trains gesture_model.h / throttle_model.h from the CSV
    gesture_data.csv         Accumulated labeled training data
unity/
  Assets/Scripts/
    IFlightInput.cs                  Common input interface (pitch/roll/yaw/throttle)
    GestureFlightInput.cs            Reads gesture_classifier.ino over serial
    AerodynamicFlightController.cs   G650ER flight model (AircraftModel) driven by IFlightInput
    WorldBootstrapper.cs             Builds the runtime scene (terrain, runway, plane, camera, HUD)
    InfiniteGround.cs                Keeps the ground under the plane so the landscape never ends
    EngineStartup.cs, TakeoffClearance.cs, CrashEffects.cs, ExplosionEffect.cs
                                     Engine spool-up gating, ATC callout, crash/explosion FX
    SmoothFlightCamera.cs, FlightHud.cs
                                     Chase camera, airspeed readout
  Assets/Models/gulfstream-g650er/   G650ER 3D model (see Credits)
  Assets/Sound/                      Engine start and ATC audio
```

## Getting started

### 1. Wire and check the hardware

Wire the three IMUs through the multiplexer, then use the Arduino IDE to confirm everything answers before you flash the real firmware:

1. Flash `glove/testing/multiplexer.ino` to scan the I2C bus and confirm that the mux and IMUs respond.
2. Flash `glove/testing/mpu.ino` to read each IMU and confirm it's on the channel you expect.

### 2. Flash the glove

Install the TFLite Micro Arduino library by cloning [tflite-micro-arduino-examples](https://github.com/tensorflow/tflite-micro-arduino-examples) into your Arduino `libraries` folder as `Arduino_TensorFlowLite`. Then flash `glove/src/gesture_classifier/gesture_classifier.ino`.

### 3. Run the simulator

Open `unity/` in Unity 6 and load `Assets/Scenes/FlightSim.unity`. `WorldBootstrapper` builds the whole scene at runtime, so you don't need to set anything up by hand. Set the glove's serial device (for example `/dev/cu.usbmodemXXXX` on macOS) in the **Glove Device Path** field under **Input Source** on the `WorldBootstrapper` object, then press Play.

Only one process can hold the glove's serial port at a time, so close any Python tools before pressing Play, and vice versa.

### 4. Capture data and train (optional)

```bash
cd glove/testing
pip install pyserial tensorflow numpy

# 1. Flash gesture_capture/gesture_capture.ino (not gesture_classifier.ino)
# 2. Record labeled reps for both the pose and throttle label groups
python3 capture_gesture_data.py --port /dev/tty.usbmodemXXXX

# 3. Train both models from the accumulated CSV
python3 train_gesture_model.py --data gesture_data.csv --group pose
python3 train_gesture_model.py --data gesture_data.csv --group throttle

# 4. Copy the generated gesture_model.h / throttle_model.h into
#    glove/src/gesture_classifier/, then reflash gesture_classifier.ino
```

Run either script with `-h` for all the options. The IMU readings are raw and uncalibrated, so if you remount the glove differently you'll need to recapture and retrain.

## Tech stack

**Glove:** Arduino (C++), TensorFlow Lite Micro, raw I2C to the MPU-6050s and TCA9548A
**Training:** Python, TensorFlow / Keras, NumPy, pyserial
**Simulator:** Unity 6 (C#), Unity rigidbody physics
**Flight model references:** 1976 US Standard Atmosphere, Lock's drag-rise rule, published G650ER specifications (sources in `AerodynamicFlightController.cs`)

## Credits

- **Gulfstream G650ER 3D model** by [Luca Martina](https://sketchfab.com/LucaMartina) on Sketchfab ([model page](https://sketchfab.com/3d-models/gulfstream-g650er-b88fa0194c2b49ef8560337ab96d7579)), licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). Converted to OBJ in Blender for use in `unity/Assets/Models/gulfstream-g650er/`.
- **TensorFlow Lite Micro** Arduino library, by the TensorFlow authors (Apache 2.0).

## AI disclosure

AI assistance (Claude) was used in this project for documentation, testing, and debugging.
