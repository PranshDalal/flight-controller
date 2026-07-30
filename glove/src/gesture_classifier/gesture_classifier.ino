#include <Wire.h>

#include <TensorFlowLite.h>
#include "tensorflow/lite/micro/micro_interpreter.h"
#include "tensorflow/lite/micro/micro_log.h"
#include "tensorflow/lite/micro/micro_mutable_op_resolver.h"
#include "tensorflow/lite/schema/schema_generated.h"

#include "gesture_model.h"
#include "throttle_model.h"

static_assert(kGestureWindowSamples == kThrottleWindowSamples,
              "pose and throttle models must be trained with the same --window size");

// Runs two TFLite Micro models on the Nano 33 BLE Sense:
//   POSE (hub):       neutral / climb / dive / bank_left / bank_right /
//                      climb_bank_left / climb_bank_right / dive_bank_left / dive_bank_right
//   THROTTLE (fingers): neutral / extend / curl
// Separate models because pose and finger curl are independent - you can
// hold a bank and extend your fingers at once. A combo pose like
// climb_bank_right is just another trained class, not a separate axis
// model, so it only works if capture_gesture_data.py actually recorded it.
//
// Pipeline: gesture_capture.ino streams raw IMU samples ->
// capture_gesture_data.py labels them into a CSV -> train_gesture_model.py
// trains gesture_model.h / throttle_model.h -> copy both headers here and
// flash. Streams "<pose>,<poseConf>,<throttle>,<throttleConf>\n", read by
// GestureFlightInput.cs.
//
// Needs the TFLite Micro Arduino library (not the deprecated
// Arduino_TensorFlowLite from the Library Manager) - clone
// https://github.com/tensorflow/tflite-micro-arduino-examples straight into
// ~/Documents/Arduino/libraries/Arduino_TensorFlowLite, it has no releases
// to download instead.
//
// Also needs the index/middle finger MPU-6050s actually wired up - check
// with mpu.ino first. Both models train on raw, uncalibrated orientation,
// so a different mount means re-recording and retraining.

#define TCAADDR 0x70
#define CH_HUB 0
#define CH_INDEX 1
#define CH_MIDDLE 2

#define MPU_ADDR 0x68
#define PWR_MGMT_1 0x6B
#define ACCEL_XOUT_H 0x3B

const unsigned long SAMPLE_INTERVAL_MS = 15; // ~66 Hz, must match the other sketches/scripts

struct SensorData {
  float ax, ay, az; // g
  float gx, gy, gz; // deg/s
};

void tcaSelect(uint8_t i) {
  if (i > 7) return;
  Wire.beginTransmission(TCAADDR);
  Wire.write(1 << i);
  Wire.endTransmission();
}

void initMPU(uint8_t channel) {
  tcaSelect(channel);
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(PWR_MGMT_1);
  Wire.write(0);
  Wire.endTransmission(true);
}

SensorData readMPU(uint8_t channel) {
  tcaSelect(channel);
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(ACCEL_XOUT_H);
  Wire.endTransmission(false);
  Wire.requestFrom(MPU_ADDR, 14, true);

  int16_t rawAx = (Wire.read() << 8) | Wire.read();
  int16_t rawAy = (Wire.read() << 8) | Wire.read();
  int16_t rawAz = (Wire.read() << 8) | Wire.read();
  Wire.read(); Wire.read(); // skip onboard temperature
  int16_t rawGx = (Wire.read() << 8) | Wire.read();
  int16_t rawGy = (Wire.read() << 8) | Wire.read();
  int16_t rawGz = (Wire.read() << 8) | Wire.read();

  SensorData data;
  data.ax = rawAx / 16384.0f;
  data.ay = rawAy / 16384.0f;
  data.az = rawAz / 16384.0f;
  data.gx = rawGx / 131.0f;
  data.gy = rawGy / 131.0f;
  data.gz = rawGz / 131.0f;
  return data;
}

// Oldest-first rolling window, all three sensors: hub in [0..5], index in
// [6..11], middle in [12..17] - same layout the training script sliced.
const int kRawAxesPerSample = 18;
float windowBuffer[kGestureWindowSamples][kRawAxesPerSample];
int samplesInBuffer = 0;

void pushSample(const SensorData &hub, const SensorData &indexF, const SensorData &middle) {
  int idx;
  if (samplesInBuffer < kGestureWindowSamples) {
    idx = samplesInBuffer;
    samplesInBuffer++;
  } else {
    for (int i = 1; i < kGestureWindowSamples; i++) {
      for (int a = 0; a < kRawAxesPerSample; a++) {
        windowBuffer[i - 1][a] = windowBuffer[i][a];
      }
    }
    idx = kGestureWindowSamples - 1;
  }
  windowBuffer[idx][0] = hub.ax;    windowBuffer[idx][1] = hub.ay;    windowBuffer[idx][2] = hub.az;
  windowBuffer[idx][3] = hub.gx;    windowBuffer[idx][4] = hub.gy;    windowBuffer[idx][5] = hub.gz;
  windowBuffer[idx][6] = indexF.ax;  windowBuffer[idx][7] = indexF.ay;  windowBuffer[idx][8] = indexF.az;
  windowBuffer[idx][9] = indexF.gx;  windowBuffer[idx][10] = indexF.gy; windowBuffer[idx][11] = indexF.gz;
  windowBuffer[idx][12] = middle.ax; windowBuffer[idx][13] = middle.ay; windowBuffer[idx][14] = middle.az;
  windowBuffer[idx][15] = middle.gx; windowBuffer[idx][16] = middle.gy; windowBuffer[idx][17] = middle.gz;
}

namespace {
const tflite::Model* poseModel = nullptr;
const tflite::Model* throttleModel = nullptr;
tflite::MicroInterpreter* poseInterpreter = nullptr;
tflite::MicroInterpreter* throttleInterpreter = nullptr;
TfLiteTensor* poseInput = nullptr;
TfLiteTensor* poseOutput = nullptr;
TfLiteTensor* throttleInput = nullptr;
TfLiteTensor* throttleOutput = nullptr;

// Both models are tiny - plenty of headroom on the nRF52840's 256KB RAM.
// Bump these if AllocateTensors() ever fails.
constexpr int kPoseArenaSize = 20 * 1024;
constexpr int kThrottleArenaSize = 20 * 1024;
alignas(16) uint8_t poseArena[kPoseArenaSize];
alignas(16) uint8_t throttleArena[kThrottleArenaSize];
}  // namespace

// RESHAPE/FULLY_CONNECTED/SOFTMAX cover both trained graphs. Re-check with
// tf.lite.Interpreter(...)._get_ops_details() if you change the architecture.
tflite::MicroMutableOpResolver<3> resolver;

void setup() {
  Wire.begin();
  Serial.begin(115200);
  while (!Serial);

  initMPU(CH_HUB);
  initMPU(CH_INDEX);
  initMPU(CH_MIDDLE);

  resolver.AddReshape();
  resolver.AddFullyConnected();
  resolver.AddSoftmax();

  poseModel = tflite::GetModel(g_gesture_model);
  if (poseModel->version() != TFLITE_SCHEMA_VERSION) {
    MicroPrintf("pose: model schema version %d doesn't match supported version %d",
                poseModel->version(), TFLITE_SCHEMA_VERSION);
    while (true) {}
  }
  static tflite::MicroInterpreter poseStaticInterpreter(poseModel, resolver, poseArena, kPoseArenaSize);
  poseInterpreter = &poseStaticInterpreter;
  if (poseInterpreter->AllocateTensors() != kTfLiteOk) {
    MicroPrintf("pose: AllocateTensors() failed - try raising kPoseArenaSize.");
    while (true) {}
  }
  poseInput = poseInterpreter->input(0);
  poseOutput = poseInterpreter->output(0);

  throttleModel = tflite::GetModel(g_throttle_model);
  if (throttleModel->version() != TFLITE_SCHEMA_VERSION) {
    MicroPrintf("throttle: model schema version %d doesn't match supported version %d",
                throttleModel->version(), TFLITE_SCHEMA_VERSION);
    while (true) {}
  }
  static tflite::MicroInterpreter throttleStaticInterpreter(throttleModel, resolver, throttleArena, kThrottleArenaSize);
  throttleInterpreter = &throttleStaticInterpreter;
  if (throttleInterpreter->AllocateTensors() != kTfLiteOk) {
    MicroPrintf("throttle: AllocateTensors() failed - try raising kThrottleArenaSize.");
    while (true) {}
  }
  throttleInput = throttleInterpreter->input(0);
  throttleOutput = throttleInterpreter->output(0);

  Serial.println("Gesture classifier ready. Streaming <pose>,<poseConf>,<throttle>,<throttleConf>.");
}

int argmax(const float* scores, int count) {
  int best = 0;
  for (int i = 1; i < count; i++) {
    if (scores[i] > scores[best]) best = i;
  }
  return best;
}

unsigned long lastSampleMillis = 0;

void runInference() {
  for (int t = 0; t < kGestureWindowSamples; t++) {
    int base = t * kGestureNumAxes;
    poseInput->data.f[base + 0] = windowBuffer[t][0] / kGestureAccelNormG;
    poseInput->data.f[base + 1] = windowBuffer[t][1] / kGestureAccelNormG;
    poseInput->data.f[base + 2] = windowBuffer[t][2] / kGestureAccelNormG;
    poseInput->data.f[base + 3] = windowBuffer[t][3] / kGestureGyroNormDps;
    poseInput->data.f[base + 4] = windowBuffer[t][4] / kGestureGyroNormDps;
    poseInput->data.f[base + 5] = windowBuffer[t][5] / kGestureGyroNormDps;
  }
  for (int t = 0; t < kThrottleWindowSamples; t++) {
    int base = t * kThrottleNumAxes;
    for (int s = 0; s < 2; s++) {  // index (columns 6..11), then middle (columns 12..17)
      int src = 6 + s * 6;
      int dst = base + s * 6;
      throttleInput->data.f[dst + 0] = windowBuffer[t][src + 0] / kThrottleAccelNormG;
      throttleInput->data.f[dst + 1] = windowBuffer[t][src + 1] / kThrottleAccelNormG;
      throttleInput->data.f[dst + 2] = windowBuffer[t][src + 2] / kThrottleAccelNormG;
      throttleInput->data.f[dst + 3] = windowBuffer[t][src + 3] / kThrottleGyroNormDps;
      throttleInput->data.f[dst + 4] = windowBuffer[t][src + 4] / kThrottleGyroNormDps;
      throttleInput->data.f[dst + 5] = windowBuffer[t][src + 5] / kThrottleGyroNormDps;
    }
  }

  if (poseInterpreter->Invoke() != kTfLiteOk) {
    MicroPrintf("pose Invoke() failed");
    return;
  }
  if (throttleInterpreter->Invoke() != kTfLiteOk) {
    MicroPrintf("throttle Invoke() failed");
    return;
  }

  int poseIdx = argmax(poseOutput->data.f, kGestureNumClasses);
  int throttleIdx = argmax(throttleOutput->data.f, kThrottleNumClasses);

  Serial.print(kGestureLabels[poseIdx]);
  Serial.print(',');
  Serial.print(poseOutput->data.f[poseIdx], 3);
  Serial.print(',');
  Serial.print(kThrottleLabels[throttleIdx]);
  Serial.print(',');
  Serial.println(throttleOutput->data.f[throttleIdx], 3);
}

void loop() {
  unsigned long now = millis();
  if (now - lastSampleMillis < SAMPLE_INTERVAL_MS) return;
  lastSampleMillis = now;

  SensorData hub = readMPU(CH_HUB);
  SensorData indexF = readMPU(CH_INDEX);
  SensorData middle = readMPU(CH_MIDDLE);
  pushSample(hub, indexF, middle);

  if (samplesInBuffer >= kGestureWindowSamples) {
    runInference();
  }
}
