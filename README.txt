JACK PEDAL - CURRENT WORKING FILES

JackPedalControl.exe       Windows calibration app (working COM-fix build)
JackPedalControl.cs        Matching C# source
Build-Windows-App.ps1      Rebuild the executable; close it first
JackPedal_PC/              Arduino sketch and required ADS1220 library
Documentation/            Wiring PDF and setup guide

START HERE
1. Read Documentation/Setup.txt and the wiring PDF.
2. Upload JackPedal_PC/JackPedal_PC.ino through the board's COM socket.
3. Close Arduino Serial Monitor, open JackPedalControl.exe, select the current port.
4. Capture released MIN, then MAX at your desired full-brake pressure.
5. Connect the board's USB socket to the PC for game-controller input.

This keeps the known-working code, including USB diagnostics.
Older versions and test sketches were moved to the workspace backups folder.
No calibration data on the ESP32 was changed by this folder cleanup.
