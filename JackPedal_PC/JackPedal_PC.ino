 
/*
 PC APP VERSION: restored from your saved PC-compatible firmware.
 UART/CP2102: app connection at 115200; native USB: gamepad.
 ADC AVDD/DVDD/REFP0 tied -> 3V3 ONLY. Sensor supply -> 3V3.
 All grounds common; REFN0 -> GND.
 Sensor signal -> AIN1 directly; remove the old resistor divider.
 ADC input is 0.4..2.4V for the 0..10 bar sensor.
 CS10, DRDY9, SCK13, MISO12, MOSI11, direct 3.3V logic.
 No resistors/dividers on MISO or DRDY. No divider on AIN1.
 Calibration/filtering uses Windows app; no OLED/buttons required.
 New EEPROM signature resets incompatible old calibration.
 Built-in gamepad retains its standard descriptor (Y brake, others neutral).
*/
#include <USB.h>
#include <USBHIDGamepad.h>
#include <SPI.h>
#include "ADS1220_WE.h"
#include <EEPROM.h>

// Jack Pneumatic ESP32-S3-DevKitC-1 N16R8 BRAKE-ONLY controller.
// Arduino settings: ESP32S3 Dev Module, USB Mode = USB-OTG (TinyUSB),
// USB CDC On Boot = Disabled, Flash Size = 16MB, PSRAM = OPI PSRAM.
// Native USB port: Windows game controller.  UART/CP2102 port: upload +
// Windows app telemetry.  This keeps joystick HID separate from Serial data.

#if !defined(CONFIG_IDF_TARGET_ESP32S3)
#error "This firmware requires an ESP32-S3."
#endif

// Native USB HID requires TinyUSB, not the hardware CDC/JTAG mode.
#if !defined(ARDUINO_USB_MODE) || ARDUINO_USB_MODE != 0
#error "Select Tools > USB Mode > USB-OTG (TinyUSB), then upload again."
#endif
#if ARDUINO_USB_CDC_ON_BOOT
#error "USB CDC On Boot is ENABLED. Set Tools > USB CDC On Boot > Disabled."
#endif
#if ARDUINO_USB_MSC_ON_BOOT
#error "USB MSC On Boot is ENABLED. Set Tools > USB MSC On Boot > Disabled."
#endif
#if ARDUINO_USB_DFU_ON_BOOT
#error "USB DFU On Boot is ENABLED. Set Tools > USB DFU On Boot > Disabled."
#endif

#define ADS1220_CS_PIN 10
#define ADS1220_DRDY_PIN 9
#define SPI_SCK 13
#define SPI_MISO 12
#define SPI_MOSI 11

// Set to 0 while testing only the ESP32-S3 USB connection without the ADS1220.
// Change to 1 only after all ADS1220 wires in the diagram are connected.
#define ADS1220_PRESENT 1

#define EEPROM_SIZE 64
#define EEPROM_MAGIC 0x4A503130UL
#define JOYSTICK_SEND_MS 4
#define TELEMETRY_MS 50
#define DEADBAND_COUNTS 5084
#define CALIBRATION_MS 4000
#define ADC_TIMEOUT_MS 500
#define ERROR_REPORT_MS 500
#define COMMAND_BUFFER_LEN 120

constexpr float ADC_REFERENCE_V = 3.3f;
constexpr float SENSOR_ZERO_V = 0.4f;
constexpr float SENSOR_FULL_V = 2.4f;
constexpr float SENSOR_MAX_BAR = 10.0f;
constexpr float PSI_PER_BAR = 14.5037738f;

#define BRAKE_ZERO_COUNTS 1016801
#define BRAKE_MAX_COUNTS 6100806
#define ADC_MAX_SIGNED 8388607

// Built-in ESP32-S3 USB gamepad. The brake is sent on the Y axis.
USBHIDGamepad gamepad;
bool usbStarted = false;
bool lastHidReportOK = false;

// Expose read-only register verification without changing the ADC library.
class CheckedADS1220 : public ADS1220_WE {
public:
  CheckedADS1220() : ADS1220_WE(&SPI, ADS1220_CS_PIN, ADS1220_DRDY_PIN, false, true) {}
  uint8_t config(uint8_t index) { return readRegister(index); }
};
CheckedADS1220 ads;

// Declare the custom type and prototype before Arduino-generated prototypes.
enum CalibrationMode { CAL_NONE, CAL_MIN, CAL_MAX };
void beginCalibration(int axis, CalibrationMode mode);

struct Settings {
  uint32_t magic;
  int32_t minimum[4];
  int32_t maximum[4];
  uint8_t alpha[4];
};

Settings settings;
// Separate EEPROM record preserves existing calibration and filter settings.
struct Deadzones { uint32_t magic; uint8_t bottom; uint8_t top; uint8_t reserved[2]; };
Deadzones deadzones;
constexpr uint32_t DEADZONE_MAGIC = 0x445A3031UL;
static_assert(sizeof(Settings) == 40, "Check legacy EEPROM layout");
static_assert(sizeof(Settings) + sizeof(Deadzones) <= EEPROM_SIZE, "EEPROM too small");
void saveDeadzones() {
  deadzones.magic = DEADZONE_MAGIC;
  EEPROM.put(sizeof(Settings), deadzones);
  EEPROM.commit();
}
void loadDeadzones() {
  EEPROM.get(sizeof(Settings), deadzones);
  if (deadzones.magic != DEADZONE_MAGIC || deadzones.bottom > 25 || deadzones.top > 25) {
    deadzones = {DEADZONE_MAGIC, 2, 8, {0, 0}};
    saveDeadzones();
  }
}
long mapBrake(int32_t value) {
  const int64_t low = settings.minimum[1], high = settings.maximum[1];
  const int64_t span = high - low;
  if (span <= 0) return 0;
  // Percentages are taken from the complete calibrated range.
  const int64_t effectiveLow = low + span * deadzones.bottom / 100;
  const int64_t effectiveHigh = high - span * deadzones.top / 100;
  if (effectiveHigh <= effectiveLow || value <= effectiveLow) return 0;
  if (value >= effectiveHigh) return 65535;
  return (long)(((int64_t)value - effectiveLow) * 65535 / (effectiveHigh - effectiveLow));
}

bool adcInitialized = false;
const char *adcFault = "ADS1220_INIT";
bool zeroSamples = false;
unsigned long zeroSince = 0;
bool adcFresh = false;
unsigned long lastAdcSample = 0;
int32_t rawBrake = 0;
int32_t filtered[4] = { 0, 0, 0, 0 };
int32_t stable[4] = { 0, 0, 0, 0 };
bool filterReady = false;
String commandLine;
bool lastTelemetryWasError = false;
unsigned long lastErrorReport = 0;


CalibrationMode calibrationMode = CAL_NONE;
int calibrationAxis = -1;
int32_t calibrationValue = 0;
unsigned long calibrationStarted = 0;

void defaultsForAxis(int axis) {
  settings.minimum[axis] = axis == 1 ? BRAKE_ZERO_COUNTS : 0;
  settings.maximum[axis] = axis == 1 ? BRAKE_MAX_COUNTS : ADC_MAX_SIGNED;
  settings.alpha[axis] = 10;
}

void saveSettings() {
  settings.magic = EEPROM_MAGIC;
  EEPROM.put(0, settings);
  EEPROM.commit();
}

void loadSettings() {
  EEPROM.get(0, settings);
  if (settings.magic != EEPROM_MAGIC) {
    for (int i = 0; i < 4; ++i) defaultsForAxis(i);
    saveSettings();
    return;
  }
  for (int i = 0; i < 4; ++i) {
    if (settings.alpha[i] < 5 || settings.alpha[i] > 100)
      settings.alpha[i] = 10;
  }
}

// Map raw sensor value to 0..65535 game axis range.
// Returns 0 if range is invalid (prevents division by zero).
long mapAxis(int32_t value, int32_t low, int32_t high) {
  if (high <= low) return 0;
  int64_t result = (int64_t)(value - low) * 65535 / (high - low);
  return constrain((long)result, 0L, 65535L);
}

// Convert raw ADC counts to brake pressure in PSI.
float brakePsi(int32_t raw) {
  const float adcV = (float)raw * ADC_REFERENCE_V / 8388608.0f;
  const float sensorV = adcV; // Direct connection: no divider.
  const float bar = constrain((sensorV - SENSOR_ZERO_V) * SENSOR_MAX_BAR /
                              (SENSOR_FULL_V - SENSOR_ZERO_V), 0.0f, SENSOR_MAX_BAR);
  return bar * PSI_PER_BAR; // Keep the Windows telemetry pressure field in PSI.
}

void sendConfig() {
  Serial.printf("CONFIG,%ld,%ld,%u,%ld,%ld,%u,%ld,%ld,%u,%ld,%ld,%u\n",
                settings.minimum[0], settings.maximum[0], settings.alpha[0],
                settings.minimum[1], settings.maximum[1], settings.alpha[1],
                settings.minimum[2], settings.maximum[2], settings.alpha[2],
                settings.minimum[3], settings.maximum[3], settings.alpha[3]);
  Serial.printf("DZ,%u,%u\n", deadzones.bottom, deadzones.top);
}

void reply(const char *status, const char *message) {
  Serial.print(status);
  Serial.print(',');
  Serial.println(message);
}

void beginCalibration(int axis, CalibrationMode mode) {
  if (axis != 1 || calibrationMode != CAL_NONE || !adcFresh || rawBrake <= 0) {
    reply("ERR", "CALIBRATION_UNAVAILABLE");
    return;
  }
  calibrationAxis = axis;
  calibrationMode = mode;
  calibrationValue = (mode == CAL_MIN) ? INT32_MAX : INT32_MIN;
  calibrationStarted = millis();
  Serial.printf("CAL,START,%d,%s\n", axis, mode == CAL_MIN ? "MIN" : "MAX");
}

void handleCommand(String line) {
  line.trim();
  if (!line.length()) return;

  if (line == "HELLO" || line == "GET") {
    // The app answers HELLO with GET: GET must not send HELLO again.
    if (line == "HELLO") reply("HELLO", "JACK_PEDAL_PC,1");
    sendConfig();
    return;
  }

  int p1 = line.indexOf(',');
  int p2 = line.indexOf(',', p1 + 1);
  String action = p1 < 0 ? line : line.substring(0, p1);
  if (action == "DEADZONE") {
    unsigned int bottom, top; char extra;
    if (sscanf(line.c_str(), "DEADZONE,%u,%u%c", &bottom, &top, &extra) != 2 || bottom > 25 || top > 25) {
      reply("ERR", "BAD_DEADZONE"); return;
    }
    if (calibrationMode != CAL_NONE) { reply("ERR", "DEADZONE_BUSY"); return; }
    deadzones.bottom = bottom; deadzones.top = top;
    saveDeadzones(); reply("OK", "DEADZONE_SAVED"); sendConfig(); return;
  }
  int axis = p1 < 0 ? -1 : line.substring(p1 + 1, p2 < 0 ? line.length() : p2).toInt();

  if (action == "MIN") {
    beginCalibration(axis, CAL_MIN);
    return;
  }
  if (action == "MAX") {
    beginCalibration(axis, CAL_MAX);
    return;
  }
  if (action == "CANCEL") {
    calibrationMode = CAL_NONE;
    calibrationAxis = -1;
    reply("OK", "CANCELLED");
    return;
  }
  if (action == "RESET" && axis == 1) {
    defaultsForAxis(axis);
    deadzones.bottom = 2; deadzones.top = 8; saveDeadzones();
    saveSettings();
    reply("OK", "RESET");
    sendConfig();
    return;
  }
  if (action == "ALPHA" && axis == 1 && p2 >= 0) {
    int value = line.substring(p2 + 1).toInt();
    value = constrain(((value + 2) / 5) * 5, 5, 100);
    settings.alpha[axis] = value;
    saveSettings();
    reply("OK", "ALPHA");
    sendConfig();
    return;
  }

  reply("ERR", "BAD_COMMAND");
}

void readCommands() {
  while (Serial.available()) {
    char c = (char)Serial.read();
    if (c == '\n') {
      handleCommand(commandLine);
      commandLine = "";
    } else if (c != '\r' && commandLine.length() < COMMAND_BUFFER_LEN) {
      commandLine += c;
    }
  }
}

void updateCalibration() {
  if (calibrationMode == CAL_NONE) return;

  int32_t value = stable[calibrationAxis];
  if (calibrationMode == CAL_MIN) {
    calibrationValue = min(calibrationValue, value);
  } else {
    calibrationValue = max(calibrationValue, value);
  }

  unsigned long elapsed = millis() - calibrationStarted;
  if (elapsed < CALIBRATION_MS) return;

  // Validation: ensure min/max are sensible.
  if (calibrationValue <= 0 ||
      (calibrationMode == CAL_MAX && calibrationValue <= settings.minimum[calibrationAxis] + DEADBAND_COUNTS)) {
    calibrationMode = CAL_NONE;
    calibrationAxis = -1;
    reply("ERR", "INVALID_CALIBRATION_CHECK_ADC");
    return;
  }

  // Apply calibration.
  if (calibrationMode == CAL_MIN) {
    settings.minimum[calibrationAxis] = calibrationValue;
  } else {
    settings.maximum[calibrationAxis] = calibrationValue;
  }
  saveSettings();

  Serial.printf("CAL,DONE,%d,%s,%ld\n", calibrationAxis,
                calibrationMode == CAL_MIN ? "MIN" : "MAX", calibrationValue);
  calibrationMode = CAL_NONE;
  calibrationAxis = -1;
  sendConfig();
}

void setup() {
  // Start HID before sensor initialisation, so Windows sees the controller even
  // if the ADS1220 wiring needs attention.
  Serial.begin(115200);
  USB.productName("Jack Pedal Brake");
  USB.manufacturerName("Jack Pedal");
  gamepad.begin(); // Register HID before starting the USB device.
  usbStarted = USB.begin();
  Serial.printf("USB_DIAG,begin=%d,mounted=%d\n", usbStarted, (bool)USB);
  commandLine.reserve(COMMAND_BUFFER_LEN + 8);

  EEPROM.begin(EEPROM_SIZE);
  loadSettings();
  loadDeadzones();

#if ADS1220_PRESENT
  SPI.begin(SPI_SCK, SPI_MISO, SPI_MOSI, ADS1220_CS_PIN);
  ads.setSPIClockSpeed(1000000);
  adcInitialized = ads.init();
  if (!adcInitialized) {
    reply("ERR", "ADS1220_INIT");
  }

  if (adcInitialized) {
    ads.setVRefSource(ADS1220_VREF_REFP0_REFN0);
    ads.setVRefValue_V(ADC_REFERENCE_V);
    ads.setGain(ADS1220_GAIN_1);
    ads.bypassPGA(true);
    ads.setDataRate(ADS1220_DR_LVL_6);
    ads.setOperatingMode(ADS1220_TURBO_MODE);
    ads.setConversionMode(ADS1220_CONTINUOUS);
    ads.setFIRFilter(ADS1220_50HZ_60HZ);
    pinMode(ADS1220_DRDY_PIN, INPUT);
    ads.setCompareChannels(ADS1220_MUX_1_AVSS);
    ads.setNonBlockingMode(true);

    // AIN1/AVSS, gain 1, PGA bypass = 0x91; turbo continuous = 0xD4;
    // REFP0/REFN0 and FIR setting = 0x50; IDAC/DRDY configuration = 0x00.
    const uint8_t expected[4] = {0x91, 0xD4, 0x50, 0x00};
    for (uint8_t i = 0; i < 4; ++i) {
      const uint8_t actual = ads.config(i);
      Serial.printf("ADC_REG,%u,%02X,%02X\n", i, actual, expected[i]);
      if (actual != expected[i]) {
        adcInitialized = false;
        adcFault = "ADS1220_CONFIG_MISMATCH";
      }
    }

    if (adcInitialized) {
      ads.start();
    }
  }
#endif

  reply("HELLO", "JACK_PEDAL_PC,1");
}

void loop() {
  static unsigned long lastJoystick = 0, lastTelemetry = 0;


  // Always responsive to commands.
  readCommands();

  bool gotSample = false;

#if ADS1220_PRESENT
  if (adcInitialized && digitalRead(ADS1220_DRDY_PIN) == LOW) {
    rawBrake = ads.getRawData(); // Nonblocking; DRDY already asserted.

    if (rawBrake == 0) {
      if (!zeroSamples) {
        zeroSamples = true;
        zeroSince = millis();
      }
    } else {
      zeroSamples = false;
    }

    lastAdcSample = millis();
    gotSample = true;

    if (!adcFresh) {
      filterReady = false;
    }
    adcFresh = true;
  }
#endif

  // Process new sample if available.
  if (gotSample) {
    if (!filterReady) {
      filtered[1] = stable[1] = rawBrake;
      filterReady = true;
    }

    // Apply exponential moving average filter.
    filtered[1] += (int32_t)(((int64_t)rawBrake - filtered[1]) * settings.alpha[1] / 100);

    // Update stable value only when deadband threshold is crossed.
    if (abs(filtered[1] - stable[1]) >= DEADBAND_COUNTS) {
      stable[1] = filtered[1];
    }

    if (rawBrake > 0) {
      updateCalibration();
    }
  }

  // Check for ADC loss (timeout, no data, etc.).
  if (!adcInitialized || !filterReady || 
      millis() - lastAdcSample > ADC_TIMEOUT_MS || 
      (zeroSamples && millis() - zeroSince > ADC_TIMEOUT_MS)) {
    adcFresh = false;

    // Cancel calibration if ADC is lost.
    if (calibrationMode != CAL_NONE) {
      calibrationMode = CAL_NONE;
      calibrationAxis = -1;
      reply("ERR", "ADC_LOST_CALIBRATION_CANCELLED");
    }
  }

  // Read time after commands and sample processing, which can start calibration.
  const unsigned long now = millis();
  const unsigned long elapsed = now - calibrationStarted;
  const unsigned long remaining = calibrationMode == CAL_NONE || elapsed >= CALIBRATION_MS
                                  ? 0UL : CALIBRATION_MS - elapsed;

  // Send gamepad report every JOYSTICK_SEND_MS.
  if (now - lastJoystick >= JOYSTICK_SEND_MS) {
    // Map brake pressure to signed 8-bit axis range: -127 (no brake) to +127 (full brake).
    uint16_t brake = adcFresh ? mapBrake(stable[1]) : 0;
    // Convert 0..65535 to -127..+127: first scale to 0..254, then subtract 127.
    int8_t yAxis = (int8_t)((int32_t)brake * 254 / 65535 - 127);
    lastHidReportOK = usbStarted && (bool)USB && gamepad.leftStick(0, yAxis);
    lastJoystick = now;
  }

  // Send telemetry every TELEMETRY_MS.
  if (Serial && now - lastTelemetry >= TELEMETRY_MS) {
    if (!adcFresh) {
      // Repeat faults for late app connections; send the error AFTER placeholder data.
      if (!lastTelemetryWasError || now - lastErrorReport >= ERROR_REPORT_MS) {
        const char *errorMsg = !adcInitialized ? adcFault : 
                               (zeroSamples ? "ADS1220_ZERO_DATA_CHECK_SPI_AIN1" : "ADS1220_NO_DATA");
        Serial.printf("T,0,0,0,0,0,0,0,0,0.0,-1,0\n");
        reply("ERR", errorMsg);
        lastErrorReport = now;
        lastTelemetryWasError = true;
      }
    } else {
      lastTelemetryWasError = false;
      Serial.printf("T,%ld,%ld,%ld,%ld,%ld,%ld,%ld,%ld,%.1f,%d,%lu\n",
                    stable[0], stable[1], stable[2], stable[3],
                    mapAxis(stable[0], settings.minimum[0], settings.maximum[0]),
                    mapBrake(stable[1]),
                    mapAxis(stable[2], settings.minimum[2], settings.maximum[2]),
                    mapAxis(stable[3], settings.minimum[3], settings.maximum[3]),
                    brakePsi(stable[1]), calibrationAxis,
                    remaining);
    }
    lastTelemetry = now;
  }

  // Periodic diagnostics are ignored by the Windows app; view via COM Serial Monitor.
  static unsigned long lastUsbDiagnostic = 0;
  if (now - lastUsbDiagnostic >= 2000) {
    Serial.printf("USB_DIAG,begin=%d,mounted=%d,report=%d\n",
                  usbStarted, (bool)USB, lastHidReportOK);
    lastUsbDiagnostic = now;
  }
  delay(1);
}
 


