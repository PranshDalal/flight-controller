#include <Wire.h>

// Glove flight controller: Arduino Nano 33 BLE Sense + TCA9548A mux + one
// MPU-6050 in the back-of-hand hub. Multiplexer's still wired in case finger
// pods come back later, but only the hub channel gets read.
//
// Streams "<pitchAxis>,<rollAxis>\n" over serial at 115200, both in [-1, 1].
// Read by GloveFlightInput.cs. Tilt hand forward/back = pitch, bank
// left/right = roll, flat = neutral. Send 'c' to re-zero, 'd' to toggle a
// verbose debug dump instead of the CSV stream.
//
// Throttle auto-ramps and yaw is fully automatic now (see
// AerodynamicFlightController), so this only ever reads/reports pitch+roll.
//
// If a control reads backwards on real hardware, flip the matching *_SIGN
// constant below instead of touching the angle math - mounting is never
// perfectly consistent build to build. Use glove/testing/mpu.ino to check
// which raw axis actually responds to which motion first.

#define TCAADDR 0x70
#define CH_HUB 0 // multiplexer channel the hub IMU is on

#define MPU_ADDR 0x68
#define PWR_MGMT_1 0x6B
#define ACCEL_XOUT_H 0x3B

#define HUB_PITCH_SIGN 1
#define HUB_ROLL_SIGN  1

struct SensorData {
  float ax, ay, az; // g
  float gx, gy, gz; // deg/s
};

struct AngleEstimate {
  float pitch = 0.0f;
  float roll  = 0.0f;
};

AngleEstimate hubAngle;

// Subtracted from every gyro reading before integrating - otherwise the
// chip's zero-rate offset drifts straight into the angle even at rest.
float gyroBiasX = 0.0f;
float gyroBiasY = 0.0f;

// Neutral pose, set by calibrate() so mounting misalignment washes out.
float hubPitchZero = 0.0f;
float hubRollZero = 0.0f;

const float COMPLEMENTARY_ALPHA = 0.98f;

// How much we trust the accelerometer fades smoothly based on how far off 1g
// it's reading, rather than a hard cutoff. A hard gate used to reject the
// accel on most samples during a normal pitch-up motion, so gyro bias ran
// unchecked long enough to show up as a fake roll alongside every climb.
const float ACCEL_TRUST_FULL_DEVIATION_G = 0.10f;
const float ACCEL_TRUST_ZERO_DEVIATION_G = 0.35f;
const float ACCEL_GATE_LOW_G  = 1.0f - ACCEL_TRUST_FULL_DEVIATION_G; // debug-dump display only
const float ACCEL_GATE_HIGH_G = 1.0f + ACCEL_TRUST_FULL_DEVIATION_G;

const float MAX_HUB_PITCH_DEG = 30.0f;
const float MAX_HUB_ROLL_DEG  = 35.0f;
const float HUB_PITCH_DEADZONE = 0.08f;
const float HUB_ROLL_DEADZONE  = 0.08f;

// Real wrists rarely pitch or roll in perfect isolation - extending the
// wrist drags in a bit of the other axis as normal anatomy, not sensor
// noise. If the plane banks every time you pitch (or vice versa), measure
// the ratio with `monitor.py --guided` and set the matching constant below;
// it's per-hand, so it starts at zero.
const float PITCH_TO_ROLL_COUPLING = 0.0f;
const float ROLL_TO_PITCH_COUPLING = 0.0f;

unsigned long lastUpdateMicros = 0;
bool debugMode = false;

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

float pitchFromAccel(const SensorData &d) {
  return atan2(-d.ax, sqrt(d.ay * d.ay + d.az * d.az)) * RAD_TO_DEG;
}

float rollFromAccel(const SensorData &d) {
  // Plain atan2(ay, az) wraps right at this hub's flat-hand neutral, so
  // ordinary noise flips the reading between +179 and -179 and shoves a
  // false step through the filter below. Negating both inputs just rotates
  // the discontinuity 180 degrees away from neutral.
  return atan2(-d.ay, -d.az) * RAD_TO_DEG;
}

// 1 = accelerometer is plausibly just reading gravity, 0 = the hand's
// actually moving and the reading can't be trusted for orientation.
float accelTrustWeight(const SensorData &d) {
  float mag = sqrt(d.ax * d.ax + d.ay * d.ay + d.az * d.az);
  float deviation = fabs(mag - 1.0f);
  if (deviation <= ACCEL_TRUST_FULL_DEVIATION_G) return 1.0f;
  if (deviation >= ACCEL_TRUST_ZERO_DEVIATION_G) return 0.0f;
  return 1.0f - (deviation - ACCEL_TRUST_FULL_DEVIATION_G)
              / (ACCEL_TRUST_ZERO_DEVIATION_G - ACCEL_TRUST_FULL_DEVIATION_G);
}

unsigned long lastTrustedMicros = 0;

// If the accelerometer's been untrustworthy for longer than this, force a
// correction through anyway instead of letting pure gyro drift run forever.
const unsigned long MAX_UNTRUSTED_MICROS = 500000UL; // 0.5s

// Shortest signed distance between two angles, so a wrap at +-180 doesn't
// read as a huge jump.
float wrapDegrees180(float deg) {
  while (deg > 180.0f) deg -= 360.0f;
  while (deg < -180.0f) deg += 360.0f;
  return deg;
}

void updateAngle(const SensorData &d, float dt, unsigned long now) {
  float gy = d.gy - gyroBiasY;
  float gx = d.gx - gyroBiasX;
  float trust = accelTrustWeight(d);
  bool overdue = (now - lastTrustedMicros) > MAX_UNTRUSTED_MICROS;
  if (trust > 0.5f) lastTrustedMicros = now;
  float correctionWeight = (1.0f - COMPLEMENTARY_ALPHA) * (overdue ? 1.0f : trust);

  hubAngle.pitch = (1.0f - correctionWeight) * (hubAngle.pitch + gy * dt)
                  + correctionWeight * pitchFromAccel(d);

  // Roll can wrap past +-180 where pitch can't, so predict from the gyro
  // first and correct by the shortest angular distance rather than blending
  // the raw accel value directly.
  float rollGyroPredicted = hubAngle.roll + gx * dt;
  float rollCorrection = wrapDegrees180(rollFromAccel(d) - rollGyroPredicted);
  hubAngle.roll = rollGyroPredicted + correctionWeight * rollCorrection;
}

float deadzone(float v, float dz) {
  if (fabs(v) < dz) return 0.0f;
  return (v - (v > 0 ? dz : -dz)) / (1.0f - dz);
}

float clamp1(float v) {
  if (v > 1.0f) return 1.0f;
  if (v < -1.0f) return -1.0f;
  return v;
}

// Averages gyro readings at rest for the bias, then settles the filter and
// records the current pose as neutral. Runs at boot and on 'c'. Keep the
// hand still for the ~2s this takes.
void calibrate() {
  float gxSum = 0.0f;
  float gySum = 0.0f;
  const int biasSamples = 100;
  for (int rep = 0; rep < biasSamples; rep++) {
    SensorData d = readMPU(CH_HUB);
    gxSum += d.gx;
    gySum += d.gy;
    delay(10);
  }
  gyroBiasX = gxSum / biasSamples;
  gyroBiasY = gySum / biasSamples;

  for (int rep = 0; rep < 100; rep++) {
    SensorData d = readMPU(CH_HUB);
    updateAngle(d, 0.01f, micros());
    delay(10);
  }

  hubPitchZero = hubAngle.pitch;
  hubRollZero = hubAngle.roll;
}

void setup() {
  Wire.begin();
  Serial.begin(115200);
  while (!Serial);

  initMPU(CH_HUB);

  Serial.println("Hold your hand flat - calibrating in 2s...");
  delay(2000);
  calibrate();
  Serial.println("Calibrated. Streaming pitch,roll.");

  lastUpdateMicros = micros();
}

void loop() {
  if (Serial.available()) {
    char cmd = Serial.read();
    if (cmd == 'c') {
      calibrate();
    } else if (cmd == 'd') {
      debugMode = !debugMode;
      Serial.println(debugMode ? "Debug mode ON (raw hub angles)" : "Debug mode OFF (streaming pitch,roll)");
    }
  }

  unsigned long now = micros();
  float dt = (now - lastUpdateMicros) / 1000000.0f;
  lastUpdateMicros = now;

  SensorData d = readMPU(CH_HUB);
  float accelMagG = sqrt(d.ax * d.ax + d.ay * d.ay + d.az * d.az); // debug dump only
  updateAngle(d, dt, now);

  // Cross-coupling correction uses each other's raw value, not the
  // already-compensated one, so the two corrections stay independent.
  float hubPitchRaw = hubAngle.pitch - hubPitchZero;
  float hubRollRaw = hubAngle.roll - hubRollZero;
  float hubPitch = hubPitchRaw - ROLL_TO_PITCH_COUPLING * hubRollRaw;
  float hubRoll = hubRollRaw - PITCH_TO_ROLL_COUPLING * hubPitchRaw;

  float pitchAxis = clamp1(HUB_PITCH_SIGN * deadzone(hubPitch / MAX_HUB_PITCH_DEG, HUB_PITCH_DEADZONE));
  float rollAxis = clamp1(HUB_ROLL_SIGN * deadzone(hubRoll / MAX_HUB_ROLL_DEG, HUB_ROLL_DEADZONE));

  if (debugMode) {
    Serial.print("HUB pitch="); Serial.print(hubAngle.pitch, 2);
    Serial.print(" roll="); Serial.print(hubAngle.roll, 2);
    Serial.print(" || hubPitch="); Serial.print(hubPitch, 2);
    Serial.print(" hubRoll="); Serial.print(hubRoll, 2);
    Serial.print(" || accelG="); Serial.println(accelMagG, 2); // should sit near 1.00 at rest
  } else {
    Serial.print(pitchAxis, 3); Serial.print(',');
    Serial.println(rollAxis, 3);
  }

  delay(15); // ~65 Hz
}
