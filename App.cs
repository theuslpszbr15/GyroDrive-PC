using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace GyroDrive
{
    public sealed class Dashboard : Form
    {
        private readonly Steering steering = new Steering();
        private readonly Timer timer = new Timer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Button center = new Button(), activate = new Button(), driver = new Button();
        private readonly TrackBar limit = new TrackBar(), deadzone = new TrackBar(), smoothing = new TrackBar();
        private readonly CheckBox invert = new CheckBox();
        private readonly Color background = Color.FromArgb(21, 23, 24), ink = Color.FromArgb(237, 242, 240), muted = Color.FromArgb(153, 165, 160), green = Color.FromArgb(106, 228, 168);
        private VirtualPad output;
        private int device = -1;
        private bool active, calibrating, fresh;
        private double lastTick, lastScan = -2, lastFresh, calibrationStart, stableStart, sumX, sumY, sumZ, angle, command;
        private int samples;
        private PadState pad;
        private ImuState imu;
        private ImuState stableImu;
        private MotionState motion;
        private string message = "Aguardando controle", virtualStatus = "Driver virtual ausente";
        private string errorDetail = "";
        private bool previousTouch;
        private bool initializing = true;
        private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GyroDrive", "settings.txt");

        public Dashboard()
        {
            Text = "GyroDrive | DualShock 4";
            ClientSize = new Size(960, 670);
            MinimumSize = new Size(880, 710);
            BackColor = background;
            ForeColor = ink;
            Font = new Font("Bahnschrift", 10);
            DoubleBuffered = true;
            StartPosition = FormStartPosition.CenterScreen;
            MakeButton(center, "Centralizar", delegate { BeginCalibration(); });
            MakeButton(activate, "Ativar direcao", delegate { ToggleOutput(); });
            MakeButton(driver, "Driver virtual", delegate { Process.Start("https://github.com/nefarius/ViGEmBus/releases/latest"); });
            ConfigureSlider(limit, 10, 85, 45);
            ConfigureSlider(deadzone, 0, 10, 2);
            ConfigureSlider(smoothing, 0, 120, 30);
            invert.Text = "Inverter direcao";
            invert.AutoSize = true;
            invert.CheckedChanged += delegate { steering.Inverted = invert.Checked; SaveSettings(); };
            Controls.Add(invert);
            LoadSettings();
            initializing = false;
            virtualStatus = VirtualPad.DriverAvailable() ? "Driver virtual disponivel" : "Driver virtual ausente";
            Resize += delegate { LayoutControls(); };
            FormClosing += delegate { Shutdown(); };
            LayoutControls();
            timer.Interval = 15;
            timer.Tick += delegate { TickSensors(); };
            timer.Start();
        }

        private void MakeButton(Button button, string text, EventHandler click)
        {
            button.Text = text;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(70, 82, 76);
            button.BackColor = Color.FromArgb(34, 39, 36);
            button.ForeColor = ink;
            button.Height = 40;
            button.Cursor = Cursors.Hand;
            button.Click += click;
            Controls.Add(button);
        }

        private void ConfigureSlider(TrackBar slider, int minimum, int maximum, int value)
        {
            slider.Minimum = minimum;
            slider.Maximum = maximum;
            slider.Value = value;
            slider.TickStyle = TickStyle.None;
            slider.BackColor = background;
            slider.ValueChanged += delegate {
                steering.LimitDegrees = limit.Value;
                steering.DeadzoneDegrees = deadzone.Value;
                steering.SmoothingMilliseconds = smoothing.Value;
                Invalidate();
                SaveSettings();
            };
            Controls.Add(slider);
        }

        private void LayoutControls()
        {
            int right = ClientSize.Width - 288;
            limit.SetBounds(right - 6, 204, 250, 40);
            deadzone.SetBounds(right - 6, 294, 250, 40);
            smoothing.SetBounds(right - 6, 384, 250, 40);
            invert.Location = new Point(right, 448);
            center.SetBounds(right, 493, 242, 40);
            activate.SetBounds(right, 543, 242, 44);
            driver.SetBounds(right, 612, 242, 28);
            Invalidate();
        }

        private void BeginCalibration()
        {
            if (!fresh) return;
            StopOutput();
            steering.Reset();
            Controller.JslSetAutomaticCalibration(device, false);
            Controller.JslResetContinuousCalibration(device);
            Controller.JslStartContinuousCalibration(device);
            calibrating = true;
            calibrationStart = clock.Elapsed.TotalSeconds;
            ResetAverage(calibrationStart);
            message = "Calibrando...";
        }

        private void ResetAverage(double now)
        {
            stableStart = now;
            sumX = sumY = sumZ = 0;
            samples = 0;
            stableImu = imu;
        }

        private void FinishCalibration()
        {
            calibrating = false;
            if (device >= 0)
            {
                Controller.JslPauseContinuousCalibration(device);
                Controller.JslSetAutomaticCalibration(device, true);
            }
        }

        private void TickSensors()
        {
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = Math.Min(0.1, Math.Max(0.001, now - lastTick));
            lastTick = now;
            try
            {
                if (device < 0 && now - lastScan > 1)
                {
                    lastScan = now;
                    device = Controller.Find();
                    if (device >= 0)
                    {
                        lastFresh = now;
                        previousTouch = false;
                        Controller.JslSetAutomaticCalibration(device, true);
                        message = "Controle conectado";
                    }
                }
                fresh = Controller.Fresh(device);
                if (!fresh)
                {
                    StopOutput();
                    steering.Reset();
                    if (calibrating) FinishCalibration();
                    pad = new PadState();
                    command = angle = 0;
                    message = "Sem sinal do controle";
                    if (device >= 0 && Controller.NeedsReconnect(Controller.JslStillConnected(device), now - lastFresh))
                    {
                        Controller.JslDisconnectAndDisposeAll();
                        device = -1;
                        previousTouch = false;
                    }
                }
                else
                {
                    lastFresh = now;
                    pad = Controller.JslGetSimpleState(device);
                    imu = Controller.JslGetIMUState(device);
                    motion = Controller.JslGetMotionState(device);
                    bool touch = (pad.Buttons & 0x20000) != 0;
                    if (touch && !previousTouch) BeginCalibration();
                    previousTouch = touch;
                    if (calibrating)
                    {
                        double speed = Math.Sqrt(imu.GyroX * imu.GyroX + imu.GyroY * imu.GyroY + imu.GyroZ * imu.GyroZ);
                        double accelChange = Math.Sqrt(Math.Pow(imu.AccelX - stableImu.AccelX, 2) + Math.Pow(imu.AccelY - stableImu.AccelY, 2) + Math.Pow(imu.AccelZ - stableImu.AccelZ, 2));
                        double gravityMagnitude = Math.Sqrt(motion.GravityX * motion.GravityX + motion.GravityY * motion.GravityY + motion.GravityZ * motion.GravityZ);
                        if (!Steering.Finite(speed) || !Steering.Finite(accelChange) || speed > 6 || accelChange > 0.04)
                        {
                            ResetAverage(now);
                            if (Controller.NeedsCalibrationRestart(speed, accelChange))
                            {
                                Controller.JslResetContinuousCalibration(device);
                                Controller.JslStartContinuousCalibration(device);
                            }
                        }
                        else if (!Steering.Finite(gravityMagnitude) || gravityMagnitude < 0.85 || gravityMagnitude > 1.15) ResetAverage(now);
                        else { sumX += motion.GravityX; sumY += motion.GravityY; sumZ += motion.GravityZ; samples++; }
                        if (now - stableStart >= 1.2 && samples > 30)
                        {
                            FinishCalibration();
                            message = steering.Center(sumX / samples, sumY / samples, sumZ / samples) ? "Centro calibrado" : "Calibracao indisponivel";
                        }
                        else if (now - calibrationStart > 6) { FinishCalibration(); message = "Calibracao interrompida"; }
                    }
                    angle = steering.Angle(motion.GravityX, motion.GravityY, motion.GravityZ);
                    command = steering.Update(angle, elapsed);
                    if (active && output != null) output.Send(pad, command);
                }
            }
            catch (Exception error)
            {
                StopOutput();
                message = "Falha de conexao";
                errorDetail = error.Message;
                Log(error.ToString());
            }
            center.Enabled = fresh && !calibrating;
            activate.Enabled = fresh && steering.Calibrated && !calibrating;
            activate.Text = active ? "Pausar direcao" : "Ativar direcao";
            activate.BackColor = active ? Color.FromArgb(35, 83, 57) : Color.FromArgb(34, 39, 36);
            Invalidate();
        }

        private void ToggleOutput()
        {
            if (active) { StopOutput(); message = "Direcao pausada"; return; }
            if (!fresh || !steering.Calibrated) return;
            try
            {
                if (output == null) output = new VirtualPad();
                active = true;
                errorDetail = "";
                virtualStatus = "Xbox virtual conectado";
                message = "Direcao ativa";
            }
            catch (Exception error)
            {
                StopOutput();
                virtualStatus = "Saida virtual indisponivel";
                errorDetail = error.Message;
                Log(error.ToString());
                MessageBox.Show(this, "A leitura do controle funciona, mas o driver virtual nao esta disponivel.\n\n" + error.Message + "\n\nO ViGEmBus e um projeto aposentado. A instalacao do driver exige sua aprovacao e pode exigir reinicio.", "Saida para o jogo", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void StopOutput()
        {
            active = false;
            if (output != null) output.Neutral();
        }

        private void Shutdown()
        {
            timer.Stop();
            if (output != null) { output.Dispose(); output = null; }
            Controller.JslDisconnectAndDisposeAll();
            SaveSettings();
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsPath)) return;
                string[] values = File.ReadAllLines(settingsPath);
                if (values.Length != 4) return;
                limit.Value = Math.Max(limit.Minimum, Math.Min(limit.Maximum, Int32.Parse(values[0])));
                deadzone.Value = Math.Max(deadzone.Minimum, Math.Min(deadzone.Maximum, Int32.Parse(values[1])));
                smoothing.Value = Math.Max(smoothing.Minimum, Math.Min(smoothing.Maximum, Int32.Parse(values[2])));
                invert.Checked = Boolean.Parse(values[3]);
            }
            catch (Exception error) { Log(error.Message); }
        }

        private void SaveSettings()
        {
            try
            {
                if (initializing) return;
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllLines(settingsPath, new string[] { limit.Value.ToString(), deadzone.Value.ToString(), smoothing.Value.ToString(), invert.Checked.ToString() });
            }
            catch (Exception error) { Log(error.Message); }
        }

        private static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GyroDrive.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Label(Graphics graphics, string text, float size, Color color, float left, float top)
        {
            using (Font font = new Font("Bahnschrift", size))
            using (Brush brush = new SolidBrush(color)) graphics.DrawString(text, font, brush, left, top);
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            Graphics graphics = args.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int right = ClientSize.Width - 288;
            using (Pen separator = new Pen(Color.FromArgb(53, 61, 57)))
            {
                graphics.DrawLine(separator, 36, 103, ClientSize.Width - 36, 103);
                graphics.DrawLine(separator, right - 35, 140, right - 35, 637);
            }
            Label(graphics, "GYRODRIVE", 26, ink, 34, 23);
            Label(graphics, "DUALSHOCK 4  /  CARX STREET", 10, muted, 36, 68);
            Label(graphics, fresh ? "CONTROLE  /  CONECTADO" : "SEM CONEXAO", 11, fresh ? green : Color.Salmon, right, 32);
            Label(graphics, active ? "SAIDA ATIVA" : "SAIDA PAUSADA", 10, active ? green : muted, right, 59);
            Label(graphics, "DIRECAO", 11, muted, 36, 126);
            Label(graphics, message, 11, fresh ? green : muted, 36, 153);
            float wheelX = (right - 35) / 2f;
            float wheelY = 337;
            GraphicsState transform = graphics.Save();
            graphics.TranslateTransform(wheelX, wheelY);
            graphics.RotateTransform((float)(command * 110));
            using (Pen rim = new Pen(Color.FromArgb(85, 100, 92), 13)) graphics.DrawEllipse(rim, -122, -122, 244, 244);
            using (Pen accent = new Pen(active ? green : Color.FromArgb(147, 177, 160), 13)) graphics.DrawArc(accent, -122, -122, 244, 244, 252, 36);
            using (Pen spoke = new Pen(Color.FromArgb(108, 123, 115), 11))
            {
                graphics.DrawLine(spoke, -111, -36, 0, 0);
                graphics.DrawLine(spoke, 111, -36, 0, 0);
                graphics.DrawLine(spoke, 0, 113, 0, 0);
            }
            using (Brush hub = new SolidBrush(background)) graphics.FillEllipse(hub, -52, -52, 104, 104);
            using (Pen outline = new Pen(Color.FromArgb(69, 84, 75), 2)) graphics.DrawEllipse(outline, -52, -52, 104, 104);
            graphics.Restore(transform);
            string percent = Math.Round(command * 100).ToString("+0;-0;0") + "%";
            using (Font font = new Font("Bahnschrift", 24))
            using (Brush brush = new SolidBrush(ink))
            {
                SizeF size = graphics.MeasureString(percent, font);
                graphics.DrawString(percent, font, brush, wheelX - size.Width / 2, wheelY - size.Height / 2);
            }
            Label(graphics, "INCLINACAO  " + angle.ToString("+0.0;-0.0;0.0") + " graus", 11, muted, wheelX - 102, 482);
            int barWidth = (right - 115) / 2;
            DrawTrigger(graphics, "L2 / FREIO", pad.LeftTrigger, 36, 541, barWidth, Color.FromArgb(244, 137, 119));
            DrawTrigger(graphics, "R2 / ACELERADOR", pad.RightTrigger, 58 + barWidth, 541, barWidth, green);
            Label(graphics, fresh ? "GIRO  " + imu.GyroX.ToString("F1") + "  /  " + imu.GyroY.ToString("F1") + "  /  " + imu.GyroZ.ToString("F1") + "  graus/s" : "GIRO  --", 9, muted, 36, 613);
            Label(graphics, "AJUSTES", 11, muted, right, 126);
            Label(graphics, "Limite de inclinacao", 11, ink, right, 166);
            Label(graphics, limit.Value + " graus", 10, green, right + 173, 166);
            Label(graphics, "Zona morta", 11, ink, right, 256);
            Label(graphics, deadzone.Value + " graus", 10, green, right + 173, 256);
            Label(graphics, "Suavizacao", 11, ink, right, 346);
            Label(graphics, smoothing.Value + " ms", 10, green, right + 173, 346);
            Label(graphics, virtualStatus, 9, muted, right, 590);
            if (errorDetail.Length > 0)
                Label(graphics, "Ver registro de conexao", 9, Color.Salmon, 36, 642);
        }

        private void DrawTrigger(Graphics graphics, string title, double value, int left, int top, int width, Color color)
        {
            value = Steering.Finite(value) ? Math.Max(0, Math.Min(1, value)) : 0;
            Label(graphics, title, 10, muted, left, top);
            Label(graphics, Math.Round(value * 100) + "%", 10, ink, left + width - 38, top);
            using (Brush track = new SolidBrush(Color.FromArgb(49, 58, 52))) graphics.FillRectangle(track, left, top + 29, width, 7);
            using (Brush fill = new SolidBrush(color)) graphics.FillRectangle(fill, left, top + 29, (float)(width * value), 7);
        }

        public void StartUiCheck(string imagePath)
        {
            Timer check = new Timer();
            Stopwatch duration = Stopwatch.StartNew();
            int stage = 0;
            int lastDiagnostic = -1;
            check.Interval = 100;
            check.Tick += delegate {
                try
                {
                    int second = (int)duration.Elapsed.TotalSeconds;
                    if (second != lastDiagnostic)
                    {
                        lastDiagnostic = second;
                        Console.WriteLine("UI: fresh={0}, stage={1}, calibrating={2}, samples={3}, gyro=({4:F2},{5:F2},{6:F2}), gravity=({7:F3},{8:F3},{9:F3}), message={10}", fresh, stage, calibrating, samples, imu.GyroX, imu.GyroY, imu.GyroZ, motion.GravityX, motion.GravityY, motion.GravityZ, message);
                    }
                    if (duration.Elapsed.TotalSeconds > 12)
                    {
                        using (Bitmap bitmap = new Bitmap(Width, Height))
                        {
                            DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                            bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        throw new Exception("UI calibration timed out; state=" + message);
                    }
                    if (stage == 0 && fresh) { BeginCalibration(); stage = 1; }
                    else if (stage == 1 && steering.Calibrated && activate.Enabled)
                    {
                        using (Bitmap bitmap = new Bitmap(Width, Height))
                        {
                            DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
                            bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.WriteLine("PASS: real sensor UI, stable calibration, activation enabled, screenshot " + imagePath);
                        check.Stop();
                        check.Dispose();
                        Close();
                    }
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error.Message);
                    Program.ExitStatus = 1;
                    check.Stop();
                    check.Dispose();
                    Close();
                }
            };
            check.Start();
        }
    }

    public static class Program
    {
        public static int ExitStatus;
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                try { SteeringTests.Run(); Controller.Tests(); VirtualPad.Tests(); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            }
            if (args.Length > 0 && args[0] == "--probe") return Controller.Probe();
            if (args.Length > 0 && args[0] == "--virtual-check")
            {
                try { using (VirtualPad pad = new VirtualPad()) { Console.WriteLine("READY: virtual Xbox controller"); } }
                catch (Exception error) { Console.WriteLine("UNAVAILABLE: " + error.Message); }
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Dashboard dashboard = new Dashboard();
            if (args.Length > 0 && args[0] == "--ui-check")
                dashboard.Shown += delegate { dashboard.StartUiCheck(args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png")); };
            Application.Run(dashboard);
            return ExitStatus;
        }
    }
}