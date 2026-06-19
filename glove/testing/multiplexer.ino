#include <Wire.h>

void setup() {
  Wire.begin();
  Serial.begin(9600);
  while (!Serial); 
  Serial.println("\nMultiplexer Scanner");
}

void loop() {
  byte error, address;
  int nDevices;

  Serial.println("Scanning:");

  nDevices = 0;
  for(address = 1; address < 127; address++) {
    Wire.beginTransmission(address);
    error = Wire.endTransmission();

    if (error == 0) {
      Serial.print("Multiplexer device found at address 0x");
      if (address < 16) Serial.print("0");
      Serial.print(address, HEX);
      Serial.println("  !");
      nDevices++;
    }
  }
  
  if (nDevices == 0)
    Serial.println("No devices found\n");
  else
    Serial.println("done\n");

  delay(5000); 
}
