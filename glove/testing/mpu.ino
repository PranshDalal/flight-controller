#include <Wire.h>

#define TCAADDR 0x70
#define MPU_ADDR 0x68
#define PWR_MGMT_1 0x6B
#define ACCEL_XOUT_H 0x3B

struct SensorData {
  float ax, ay, az;
  float gx, gy, gz;
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

  SensorData data;
  data.ax = (Wire.read() << 8 | Wire.read()) / 16384.0;
  data.ay = (Wire.read() << 8 | Wire.read()) / 16384.0;
  data.az = (Wire.read() << 8 | Wire.read()) / 16384.0;
  Wire.read(); Wire.read();
  data.gx = (Wire.read() << 8 | Wire.read()) / 131.0;
  data.gy = (Wire.read() << 8 | Wire.read()) / 131.0;
  data.gz = (Wire.read() << 8 | Wire.read()) / 131.0;
  
  return data;
}

void setup() {
  Wire.begin();
  Serial.begin(9600);
  while (!Serial);

  for (uint8_t i = 0; i < 3; i++) {
    initMPU(i);
  }
  Serial.println("System Initialized. Reading data:");
}

void loop() {
  for (uint8_t i = 0; i < 3; i++) {
    SensorData finger = readMPU(i);
    
    Serial.print("Sensor "); Serial.print(i);
    Serial.print(" | Accel: "); Serial.print(finger.ax); Serial.print(", "); Serial.print(finger.ay); Serial.print(", "); Serial.println(finger.az);
  }
  
  Serial.println("---");
  delay(200);
}
