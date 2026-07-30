#include <Wire.h>

// Streams raw, unfiltered readings from all three MPU-6050s (hub, index,
// middle) for capture_gesture_data.py to record and label - unlike
// flight_controller.ino, which reports a filtered/zero-calibrated angle
// instead of raw samples.
//
// One CSV line per sample, hub then index then middle, 6 raw axes each:
//   "<hAX>,<hAY>,<hAZ>,<hGX>,<hGY>,<hGZ>,<iAX>,...,<iGZ>,<mAX>,...,<mGZ>\n"
//
// Needs the index/middle finger pods actually wired to the multiplexer - a
// missing sensor won't error, it'll just quietly feed garbage into the
// training data. Check with mpu.ino / multiplexer.ino first if unsure.
//
// SAMPLE_INTERVAL_MS has to stay in sync with capture_gesture_data.py,
// train_gesture_model.py, and gesture_classifier.ino - it's baked into the
// trained models' expected timing.

#define TCAADDR 0x70
#define CH_HUB 0
#define CH_INDEX 1
#define CH_MIDDLE 2

#define MPU_ADDR 0x68
#define PWR_MGMT_1 0x6B
#define ACCEL_XOUT_H 0x3B

const unsigned long SAMPLE_INTERVAL_MS = 15; // ~66 Hz

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

void printSensor(const SensorData &d) {
  Serial.print(d.ax, 4); Serial.print(',');
  Serial.print(d.ay, 4); Serial.print(',');
  Serial.print(d.az, 4); Serial.print(',');
  Serial.print(d.gx, 3); Serial.print(',');
  Serial.print(d.gy, 3); Serial.print(',');
  Serial.print(d.gz, 3);
}

unsigned long lastSampleMillis = 0;

void setup() {
  Wire.begin();
  Serial.begin(115200);
  while (!Serial);
  initMPU(CH_HUB);
  initMPU(CH_INDEX);
  initMPU(CH_MIDDLE);
  lastSampleMillis = millis();
}

void loop() {
  unsigned long now = millis();
  if (now - lastSampleMillis < SAMPLE_INTERVAL_MS) return;
  lastSampleMillis = now;

  SensorData hub = readMPU(CH_HUB);
  SensorData indexF = readMPU(CH_INDEX);
  SensorData middle = readMPU(CH_MIDDLE);

  printSensor(hub);
  Serial.print(',');
  printSensor(indexF);
  Serial.print(',');
  printSensor(middle);
  Serial.println();
}
