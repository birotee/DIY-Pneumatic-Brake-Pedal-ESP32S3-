JackPedal PC - 3.3V supply, 0.4-2.4V output, 0-10 bar sensor

Open JackPedal_PC.ino with ADS1220_WE.h and ADS1220_WE.cpp alongside it.
Sensor supply + -> ESP32 3V3 (not 5V).
Sensor ground -> common GND.
Sensor output -> ADS1220 AIN1 directly. Remove the old signal divider.
Identify sensor wires from its label; do not assume the old sensor colors.
ADC AVDD, DVDD, REFP0 -> 3V3.
ADC AGND/AVSS, DGND, REFN0 and CLK -> common GND. CLK is separate from SCLK.
CS GPIO10, DRDY GPIO9, SCK GPIO13, MISO GPIO12, MOSI GPIO11.
All SPI and DRDY connections direct; no resistor dividers.

Reference 3.3V, gain 1, PGA bypass, SPI 1 MHz.
Pressure bar = clamp((AIN1 volts - 0.4) * 5, 0, 10).
Windows pressure display remains PSI: bar * 14.5037738 (maximum 145.0 PSI).
Expected signal: 0 bar = 0.4V; 5 bar = 1.4V; 10 bar = 2.4V.
New EEPROM signature resets the previous sensor calibration on first boot.
Capture released MIN then desired full-brake MAX. Progress and filtering retained.

Board: ESP32S3 Dev Module, USB-OTG (TinyUSB), USB CDC On Boot Disabled.
N16R8: Flash 16MB, Flash Mode QIO 80MHz, PSRAM OPI. USB MSC and DFU On Boot Disabled.
UART/CP2102 USB for upload and Windows app, 115200 baud.
Native USB for game controller (brake on Y, other axes neutral).
Close Arduino Serial Monitor before connecting the Windows app.
Select the actual COM port; it may differ from COM6 on a new board.

ADC register verification and missing/zero-data errors retained.
User confirmed sensor readings after replacing the ADS1220 and later confirmed gamepad detection.
USB_DIAG messages remain enabled in this known-working firmware. They are ignored by the app.


BOTTOM / TOP DEADZONES
Upload the updated Arduino sketch before using the new app controls.
Each setting is 0-25 percent of the captured MIN-to-MAX range.
Defaults: bottom 2%, top 8%. Click Apply to save on the ESP32.
Bottom 2%: output stays zero through the first 2% of calibrated range.
Top 8%: output reaches 100% at 92% of calibrated range.
The remaining range is mapped linearly. PSI and voltage are unaffected.
Saved MIN/MAX and response settings are preserved when upgrading this version.
Reset restores deadzones to 2%/8%, alongside calibration/filter defaults.
Old firmware leaves the app deadzone controls disabled; upload the new sketch.
App compiled and parser tests passed; mapping arithmetic checked independently.
Firmware still requires Arduino Verify/upload and a physical pedal test.
