using System;
using System.Drawing;
using System.Globalization;
using System.IO.Ports;
using System.Windows.Forms;

namespace JackPedalControl
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    public sealed class MainForm : Form
    {
        readonly string[] names = { "Clutch", "Brake", "Gas", "Hand brake" };
        readonly Color accent = Color.FromArgb(27, 190, 155);
        readonly ComboBox ports = new ComboBox();
        readonly Button connect = new Button();
        readonly Label status = new Label();
        readonly Label psi = new Label();
        readonly Label debugValues = new Label();
        readonly Label captureStatus = new Label();
        readonly ProgressBar captureProgress = new ProgressBar();
        readonly Label telemetryStatus = new Label();
        DateTime lastTelemetry = DateTime.MinValue;
        DateTime captureRequested = DateTime.MinValue;
        string captureKind = "";
        readonly ProgressBar[] bars = new ProgressBar[4];
        readonly Label[] values = new Label[4];
        readonly Label[] ranges = new Label[4];
        readonly TrackBar[] filters = new TrackBar[4];
        readonly Label[] filterValues = new Label[4];
        readonly Timer timer = new Timer();
        readonly SerialPort serial = new SerialPort();
        string receiveBuffer = "";
        bool updatingFilters;
        bool adcErrorActive;
        bool validSample;
        bool configReceived;
        readonly NumericUpDown bottomZone = new NumericUpDown();
        readonly NumericUpDown topZone = new NumericUpDown();
        readonly Button applyZones = new Button();
        readonly Label zoneStatus = new Label();
        bool deadzonesSupported;

        DateTime lastHandshake = DateTime.MinValue;
        DateTime connectedAt = DateTime.MinValue;
        int calibrationAxis = -1;

        public MainForm()
        {
            Text = "Jack Pedal Control - 10 bar Debug";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(600, 750);
            Size = new Size(600, 750);
            MaximizeBox = false;
            BackColor = Color.FromArgb(20, 25, 32);
            ForeColor = Color.WhiteSmoke;
            Font = new Font("Segoe UI", 10F);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 10), Margin = new Padding(0) };
            var title = new Label { Text = "JACK PEDAL", AutoSize = true, Font = new Font("Segoe UI Semibold", 20F), Location = new Point(20, 12) };
            status.Text = "Disconnected"; status.AutoSize = false; status.Size = new Size(550, 24); status.AutoEllipsis = true; status.ForeColor = Color.Silver; status.Location = new Point(23, 51);
            ports.DropDownStyle = ComboBoxStyle.DropDownList; ports.Width = 100; ports.Location = new Point(255, 25);
            var refresh = MakeButton("Refresh", 363, 23, 72); refresh.Click += delegate { RefreshPorts(); };
            connect.Text = "Connect"; connect.Location = new Point(443, 23); connect.Width = 90; connect.Height = 32;
            StyleButton(connect, accent); connect.Click += ConnectClick;
            header.Controls.AddRange(new Control[] { title, status, ports, refresh, connect });
            root.Controls.Add(header, 0, 0);

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 4, 18, 18), ColumnCount = 1, RowCount = 1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.Controls.Add(CreateAxisCard(1), 0, 0);
            root.Controls.Add(body, 0, 1);

            timer.Interval = 25; timer.Tick += TimerTick; timer.Start();
            FormClosing += delegate { if (serial.IsOpen) try { serial.Close(); } catch {} };
            RefreshPorts();
        }

        Panel CreateAxisCard(int axis)
        {
            var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(7), Padding = new Padding(16), BackColor = Color.FromArgb(31, 38, 48) };
            var name = new Label { Text = names[axis].ToUpperInvariant(), AutoSize = true, Font = new Font("Segoe UI Semibold", 14F), Location = new Point(16, 13) };
            values[axis] = new Label { Text = "0.0%", AutoSize = true, Font = new Font("Segoe UI Semibold", 14F), ForeColor = accent, Location = new Point(355, 13) };
            bars[axis] = new ProgressBar { Minimum = 0, Maximum = 65535, Value = 0, Location = new Point(20, 55), Width = 410, Height = 25 };
            ranges[axis] = new Label { Text = "Calibration: waiting for controller", AutoSize = true, ForeColor = Color.Silver, Location = new Point(20, 89) };
            var smooth = new Label { Text = "Response", AutoSize = true, ForeColor = Color.Gainsboro, Location = new Point(20, 124) };
            filters[axis] = new TrackBar { Minimum = 5, Maximum = 100, TickFrequency = 5, SmallChange = 5, LargeChange = 5, Value = 10, Width = 275, Location = new Point(88, 112) };
            filterValues[axis] = new Label { Text = "10%", AutoSize = true, Location = new Point(370, 124) };
            int captured = axis;
            filters[axis].Scroll += delegate { filterValues[captured].Text = filters[captured].Value + "%"; };
            filters[axis].MouseUp += delegate { if (!updatingFilters) Send("ALPHA," + captured + "," + filters[captured].Value); };
            // FIX: also handle keyboard change
            filters[axis].KeyUp += delegate { if (!updatingFilters) Send("ALPHA," + captured + "," + filters[captured].Value); };

            var setMin = MakeButton("Capture MIN", 20, 164, 112);
            var setMax = MakeButton("Capture MAX", 140, 164, 112);
            var reset = MakeButton("Reset", 260, 164, 80);
            setMin.Click += delegate { StartCalibration(captured, "MIN"); };
            setMax.Click += delegate { StartCalibration(captured, "MAX"); };
            reset.Click += delegate { if (MessageBox.Show("Reset " + names[captured] + " calibration, filter and deadzones?", "Confirm reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { Send("CANCEL"); Send("RESET," + captured); } };
            card.Controls.AddRange(new Control[] { name, values[axis], bars[axis], ranges[axis], smooth, filters[axis], filterValues[axis], setMin, setMax, reset });
            if (axis == 1)
            {
                psi.Text = "0.0 PSI"; psi.AutoSize = true; psi.Font = new Font("Segoe UI Semibold", 11F); psi.ForeColor = Color.FromArgb(255, 184, 77); psi.Location = new Point(385, 91); card.Controls.Add(psi);
            }
            // FIX: removed duplicate wiring.Location assignment
            var wiring = new Label { Text = "Sensor power: 3.3 V required | Output: 0.4-2.4 V", AutoSize = true, ForeColor = Color.Silver, Location = new Point(20, 270) };
            captureStatus.Text = "Calibration ready: capture MIN, then MAX";
            captureStatus.Location = new Point(20, 207); captureStatus.Size = new Size(490, 24);
            captureProgress.Location = new Point(20, 238); captureProgress.Size = new Size(465, 18);
            captureProgress.Minimum = 0; captureProgress.Maximum = 4000;
            debugValues.Text = "ADC counts (filtered): --\nAIN1 voltage (calculated): --\nSensor voltage (estimated): --\nMapped brake value: --";
            debugValues.Location = new Point(20, 304); debugValues.Size = new Size(490, 100);
            debugValues.Font = new Font("Consolas", 10F);
            telemetryStatus.Text = "No telemetry received";
            telemetryStatus.Location = new Point(20, 413); telemetryStatus.Size = new Size(490, 24);
            telemetryStatus.ForeColor = Color.Silver;
            var note = new Label { Text = "AIN1 direct (no divider); ADC CLK to GND. Reference: 3.3 V.\nSignal voltage is calculated from ADC counts; supply is not measured.", Location = new Point(20, 442), Size = new Size(490, 42), ForeColor = Color.Silver, Font = new Font("Segoe UI", 9F) };
            card.Controls.AddRange(new Control[] { wiring, captureStatus, captureProgress, debugValues, telemetryStatus, note });
            // Make space between response slider and capture controls.
            foreach (Control control in card.Controls) if (control.Top >= 164) control.Top += 70;
            var bottomLabel = new Label {Text="Bottom %", Location=new Point(20,162), AutoSize=true};
            bottomZone.Location=new Point(100,158); bottomZone.Size=new Size(58,26); bottomZone.Maximum=25;
            var topLabel = new Label {Text="Top %", Location=new Point(175,162), AutoSize=true};
            topZone.Location=new Point(235,158); topZone.Size=new Size(58,26); topZone.Maximum=25;
            applyZones.Text="Apply"; applyZones.Location=new Point(315,155); applyZones.Size=new Size(80,32);
            StyleButton(applyZones,accent);
            applyZones.Click += delegate {
                if (!serial.IsOpen || !deadzonesSupported) return;
                if (calibrationAxis >= 0) { MessageBox.Show("Finish calibration before changing deadzones."); return; }
                zoneStatus.Text="Saving deadzones...";
                Send("DEADZONE,"+bottomZone.Value.ToString(CultureInfo.InvariantCulture)+","+topZone.Value.ToString(CultureInfo.InvariantCulture));
            };
            zoneStatus.Location=new Point(20,195); zoneStatus.Size=new Size(490,24);
            zoneStatus.Font=new Font("Segoe UI",9F); zoneStatus.Text="Connect and upload deadzone firmware to enable.";
            bottomZone.Enabled=topZone.Enabled=applyZones.Enabled=false;
            card.Controls.AddRange(new Control[]{bottomLabel,bottomZone,topLabel,topZone,applyZones,zoneStatus});
            return card;
        }

        Button MakeButton(string text, int x, int y, int width)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Width = width, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(46, 56, 68), ForeColor = Color.White };
            b.FlatAppearance.BorderColor = Color.FromArgb(76, 88, 102); return b;
        }

        void StyleButton(Button b, Color color)
        {
            b.FlatStyle = FlatStyle.Flat; b.BackColor = color; b.ForeColor = Color.FromArgb(12, 35, 31); b.FlatAppearance.BorderSize = 0; b.Font = new Font("Segoe UI Semibold", 9.5F);
        }

        void RefreshPorts()
        {
            string selected = ports.SelectedItem as string;
            ports.Items.Clear();
            string[] found = SerialPort.GetPortNames(); Array.Sort(found);
            ports.Items.AddRange(found);
            if (selected != null && ports.Items.Contains(selected)) ports.SelectedItem = selected;
            else if (ports.Items.Count > 0) ports.SelectedIndex = ports.Items.Count - 1;
        }

        void ConnectClick(object sender, EventArgs e)
        {
            if (serial.IsOpen) { try { serial.Close(); } catch {} SetConnected(false, "Disconnected"); return; }
            if (ports.SelectedItem == null) { MessageBox.Show("No COM port found. Connect the ESP32-S3 and press Refresh."); return; }
            try {
                serial.PortName = ports.SelectedItem.ToString(); serial.BaudRate = 115200;
                serial.NewLine = "\n"; serial.ReadTimeout = 20; serial.WriteTimeout = 500; serial.Handshake = Handshake.None; serial.DtrEnable = false; serial.RtsEnable = false;
                serial.Open(); receiveBuffer = ""; SetConnected(true, "Connected to " + serial.PortName); Send("HELLO");
            } catch (Exception ex) { SetConnected(false, "Connection failed"); MessageBox.Show(ex.Message, "Serial connection", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void SetConnected(bool yes, string text)
        {
            deadzonesSupported=false; bottomZone.Enabled=topZone.Enabled=applyZones.Enabled=false;
            zoneStatus.Text="Waiting for controller deadzone settings";
            validSample = false; adcErrorActive = false; configReceived = false; lastHandshake = DateTime.MinValue;
            lastTelemetry = DateTime.MinValue; connectedAt = yes ? DateTime.UtcNow : DateTime.MinValue;
            if (!yes) {
                calibrationAxis = -1; captureRequested = DateTime.MinValue;
                captureStatus.Text = "Capture stopped: disconnected"; captureProgress.Value = 0;
                telemetryStatus.Text = "Disconnected - displayed readings are not live";
            } else { lastTelemetry = DateTime.MinValue; telemetryStatus.Text = "Waiting for telemetry"; }
            connect.Text = yes ? "Disconnect" : "Connect"; status.Text = text; status.ForeColor = yes ? accent : Color.Silver; ports.Enabled = !yes;
        }

        void Send(string line)
        {
            if (!serial.IsOpen) { MessageBox.Show("Connect to the ESP32-S3 first."); return; }
            try { serial.WriteLine(line); } catch (Exception ex) { DisconnectForError(ex); }
        }

        void StartCalibration(int axis, string kind)
        {
            if (!serial.IsOpen) { MessageBox.Show("Connect to the ESP32-S3 first."); return; }
            if (!validSample || adcErrorActive || (DateTime.UtcNow - lastTelemetry).TotalSeconds > 1.5) { MessageBox.Show("Wait for live sensor readings before capturing calibration."); return; }
            if (calibrationAxis >= 0) { MessageBox.Show("A calibration is already running."); return; }
            captureKind = kind; captureRequested = DateTime.UtcNow;
            captureProgress.Value = 0; captureStatus.Text = "Waiting for " + kind + " capture confirmation...";
            calibrationAxis = axis; status.Text = kind == "MIN" ? "Release the pedal — capturing minimum for 4 seconds" : "Press the pedal fully — capturing maximum for 4 seconds";
            status.ForeColor = Color.FromArgb(255, 184, 77); Send(kind + "," + axis);
        }

        void TimerTick(object sender, EventArgs e)
        {
            if (!serial.IsOpen) return;
            if (!configReceived && (DateTime.UtcNow - lastHandshake).TotalSeconds >= 1) {
                lastHandshake = DateTime.UtcNow;
                Send("GET");
                if (!serial.IsOpen) return;
            }
            if (lastTelemetry == DateTime.MinValue && (DateTime.UtcNow - connectedAt).TotalSeconds > 3)
                telemetryStatus.Text = "No data: press RST once; check COM port and firmware.";
            if (lastTelemetry != DateTime.MinValue && (DateTime.UtcNow - lastTelemetry).TotalSeconds > 1.5)
                telemetryStatus.Text = "Telemetry stale - readings are not live";
            if (calibrationAxis >= 0 && captureRequested != DateTime.MinValue && (DateTime.UtcNow - captureRequested).TotalSeconds > 7) {
                calibrationAxis = -1; captureRequested = DateTime.MinValue;
                captureStatus.Text = "Capture timed out - no completion received";
                captureProgress.Value = 0;
            }
            try {
                string chunk = serial.ReadExisting(); if (chunk.Length == 0) return;
                receiveBuffer += chunk.Replace("\r", "");
                if (receiveBuffer.Length > 16384) { receiveBuffer = ""; telemetryStatus.Text = "Invalid serial stream - check firmware and baud rate"; return; }
                int newline;
                while ((newline = receiveBuffer.IndexOf('\n')) >= 0) { string line = receiveBuffer.Substring(0, newline); receiveBuffer = receiveBuffer.Substring(newline + 1); Parse(line.Trim()); }
            } catch (Exception ex) { DisconnectForError(ex); }
        }

        void Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // FIX: ignore debug ADC_REG lines that firmware sends on startup
            if (line.StartsWith("ADC_REG")) return;
            string[] p = line.Split(',');
            if (p.Length == 0) return;
            if (p[0] == "HELLO") { status.Text = "Connected — controller ready"; status.ForeColor = accent; Send("GET"); }
            else if (p[0] == "OK") {
                if (p.Length > 1 && (p[1] == "CANCELLED" || p[1] == "RESET")) {
                    calibrationAxis = -1; captureRequested = DateTime.MinValue;
                    captureProgress.Value = 0;
                    captureStatus.Text = p[1] == "RESET" ? "Calibration reset: capture MIN, then MAX" : "Capture cancelled";
                }
                if (!adcErrorActive) { status.Text = p.Length > 1 ? p[1] : "OK"; status.ForeColor = accent; }
            }
            else if (p[0] == "DZ" && p.Length == 3) {
                int bottom, top;
                if (!int.TryParse(p[1], out bottom) || !int.TryParse(p[2], out top) || bottom<0 || bottom>25 || top<0 || top>25) return;
                deadzonesSupported=true;
                bottomZone.Value=bottom; topZone.Value=top;
                bottomZone.Enabled=topZone.Enabled=applyZones.Enabled=true;
                zoneStatus.Text="Saved: 0% below "+bottom+"% of range; 100% from "+(100-top)+"%.";
            }
            else if (p[0] == "CONFIG" && p.Length >= 13) {
                configReceived = true; updatingFilters = true;
                int i = 1;
                int baseIndex = 1 + i * 3;
                ranges[i].Text = "Min " + p[baseIndex] + "     Max " + p[baseIndex + 1];
                int alpha; if (int.TryParse(p[baseIndex + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out alpha)) { alpha = Math.Max(5, Math.Min(100, alpha)); filters[i].Value = alpha; filterValues[i].Text = alpha + "%"; }
                updatingFilters = false;
            } else if (p[0] == "T" && p.Length >= 12) {
                int checkedCounts, checkedMapped, checkedActive, checkedRemaining;
                double checkedPsi;
                if (!int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out checkedCounts) ||
                    !int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out checkedMapped) ||
                    !int.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out checkedActive) ||
                    !int.TryParse(p[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out checkedRemaining) ||
                    !double.TryParse(p[9], NumberStyles.Float, CultureInfo.InvariantCulture, out checkedPsi) ||
                    double.IsNaN(checkedPsi) || double.IsInfinity(checkedPsi) ||
                    checkedCounts < -8388608 || checkedCounts > 8388607 ||
                    checkedMapped < 0 || checkedMapped > 65535 || checkedPsi < 0 || checkedPsi > 145.1 ||
                    (checkedActive != -1 && checkedActive != 1) || checkedRemaining < 0 || checkedRemaining > 4000) return;
                bool firstSample = lastTelemetry == DateTime.MinValue;
                validSample = checkedCounts > 0;
                if (firstSample && validSample && !adcErrorActive) { status.Text = "Connected - sensor readings live"; status.ForeColor = accent; }

                int mapped; if (int.TryParse(p[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out mapped)) { mapped = Math.Max(0, Math.Min(65535, mapped)); bars[1].Value = mapped; values[1].Text = (mapped * 100.0 / 65535.0).ToString("0.0", CultureInfo.InvariantCulture) + "%"; }
                psi.Text = p[9] + " PSI";
                lastTelemetry = DateTime.UtcNow;
                telemetryStatus.Text = "Live telemetry - " + DateTime.Now.ToString("HH:mm:ss");
                if (!validSample) telemetryStatus.Text = "No valid ADC reading - check controller error";
                telemetryStatus.ForeColor = Color.Silver;
                int counts;
                if (int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out counts)) {
                    // Zero telemetry is the firmware's fault placeholder, not recovery.
                    if (adcErrorActive && counts > 0 && counts <= 8388607) {
                        adcErrorActive = false;
                        status.Text = "Connected — sensor readings restored";
                        status.ForeColor = accent;
                    } else if (adcErrorActive) {
                        telemetryStatus.Text = "Controller error - readings may be invalid";
                    }
                    double ain = counts * 3.3 / 8388608.0;
                    debugValues.Text = "ADC counts (filtered): " + counts +
                        "\nAIN1 voltage (calculated): " + ain.ToString("0.0000", CultureInfo.InvariantCulture) + " V" +
                        "\nSensor voltage (estimated): " + ain.ToString("0.0000", CultureInfo.InvariantCulture) + " V" +
                        "\nMapped brake value: " + p[6] + " / 65535";
                }
                int active, remaining; if (int.TryParse(p[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out active) && int.TryParse(p[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out remaining) && active == 1) {
                    if (captureRequested == DateTime.MinValue) captureRequested = DateTime.UtcNow;
                    captureProgress.Value = Math.Max(0, Math.Min(4000, 4000 - remaining));
                    captureStatus.Text = "Capturing " + captureKind + " - " + (Math.Max(0, remaining) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s remaining";
                    calibrationAxis = active; status.Text = "Calibrating " + names[active] + " — " + (remaining / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s"; }
                else if (calibrationAxis >= 0 && active == -1) {
                    // telemetry shows no active calibration but UI thinks there is one - wait for CAL,DONE
                }
            } else if (p[0] == "CAL" && p.Length >= 4 && p[1] == "START") {
                captureKind = p[3]; calibrationAxis = 1; captureRequested = DateTime.UtcNow;
                captureProgress.Value = 0;
                captureStatus.Text = "Capturing " + captureKind + " - 4.0 s remaining";
            } else if (p[0] == "CAL" && p.Length >= 5 && p[1] == "DONE") {
                calibrationAxis = -1; captureRequested = DateTime.MinValue;
                captureProgress.Value = 4000;
                captureStatus.Text = p[3] + " saved - " + p[4] + " counts";
                status.Text = "Calibration saved"; status.ForeColor = accent;
            }
            else if (p[0] == "ERR") {
                // FIX: dont overwrite telemetryStatus for non-critical spam, only show in status bar
                // The firmware now rate-limits ERR, so this is safe to display
                if (calibrationAxis >= 0) { captureStatus.Text = "Capture stopped: " + (p.Length > 1 ? p[1] : "controller error"); captureProgress.Value = 0; }
                calibrationAxis = -1; captureRequested = DateTime.MinValue;
                // Only mark telemetry invalid for real ADC faults
                if (p.Length > 1 && (p[1].Contains("ADS1220") || p[1] == "ADC_LOST_CALIBRATION_CANCELLED")) { adcErrorActive = true; validSample = false; telemetryStatus.Text = "Controller error - readings may be invalid"; }
                status.Text = "Controller: " + (p.Length > 1 ? p[1] : "unknown"); status.ForeColor = Color.Salmon;
            }
        }

        void DisconnectForError(Exception ex)
        {
            try { serial.Close(); } catch { } SetConnected(false, "Disconnected: " + ex.Message);
        }
    }
}



