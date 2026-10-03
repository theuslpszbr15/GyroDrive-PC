using System;

namespace GyroDrive
{
    public sealed class Steering
    {
        private double baseX, baseY, baseZ, filtered;
        public bool Calibrated { get; private set; }
        public double LimitDegrees = 45;
        public double DeadzoneDegrees = 2;
        public double SmoothingMilliseconds = 30;
        public bool Inverted;

        public void Reset()
        {
            Calibrated = false;
            filtered = 0;
        }

        public bool Center(double gravityX, double gravityY, double gravityZ)
        {
            double magnitude = Math.Sqrt(gravityX * gravityX + gravityY * gravityY + gravityZ * gravityZ);
            double vertical = Math.Sqrt(gravityY * gravityY + gravityZ * gravityZ);
            if (!Finite(magnitude) || magnitude < 0.5 || vertical < 0.25) return false;
            baseX = gravityX / magnitude;
            baseY = gravityY / magnitude;
            baseZ = gravityZ / magnitude;
            filtered = 0;
            Calibrated = true;
            return true;
        }

        public double Angle(double gravityX, double gravityY, double gravityZ)
        {
            if (!Calibrated || !Finite(gravityX) || !Finite(gravityY) || !Finite(gravityZ)) return 0;
            double vertical = Math.Sqrt(baseY * baseY + baseZ * baseZ);
            double projection = (gravityY * baseY + gravityZ * baseZ) / vertical;
            double neutral = Math.Atan2(baseX, vertical);
            double angle = Math.Atan2(gravityX, projection) - neutral;
            return Math.Atan2(Math.Sin(angle), Math.Cos(angle)) * 180 / Math.PI;
        }

        public double Update(double angle, double elapsedSeconds)
        {
            if (!Calibrated || !Finite(angle) || !Finite(elapsedSeconds) || elapsedSeconds <= 0)
            {
                filtered = 0;
                return 0;
            }
            double limit = Math.Max(5, Math.Min(85, LimitDegrees));
            double deadzone = Math.Max(0, Math.Min(limit - 1, DeadzoneDegrees));
            double magnitude = Math.Max(0, Math.Abs(angle) - deadzone) / (limit - deadzone);
            double target = Math.Sign(angle) * Math.Min(1, magnitude) * (Inverted ? -1 : 1);
            double alpha = SmoothingMilliseconds <= 0 ? 1 : 1 - Math.Exp(-elapsedSeconds / (SmoothingMilliseconds / 1000));
            filtered += (target - filtered) * alpha;
            return filtered;
        }

        public static bool Finite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value);
        }
    }

    public static class SteeringTests
    {
        private static void Equal(double actual, double expected, string name)
        {
            if (Math.Abs(actual - expected) > 0.00001 || !Steering.Finite(actual))
                throw new Exception(name + ": expected " + expected + ", got " + actual);
        }

        public static void Run()
        {
            Steering steering = new Steering();
            Equal(steering.Update(30, 0.01), 0, "uncalibrated is neutral");
            if (steering.Center(0, 0, 0) || steering.Center(Double.NaN, 1, 0))
                throw new Exception("invalid calibration accepted");
            if (!steering.Center(0, -1, 0)) throw new Exception("valid calibration rejected");
            Equal(steering.Angle(0, -1, 0), 0, "neutral gravity");
            Equal(steering.Angle(0.5, -Math.Sqrt(0.75), 0), 30, "held tilt angle");
            Equal(steering.Angle(-0.5, -Math.Sqrt(0.75), 0), -30, "opposite tilt angle");
            steering.SmoothingMilliseconds = 0;
            Equal(steering.Update(1, 0.01), 0, "deadzone");
            Equal(steering.Update(45, 0.01), 1, "right limit");
            Equal(steering.Update(-90, 0.01), -1, "left clamping");
            Equal(steering.Update(23.5, 0.01), 0.5, "proportional steering");
            Equal(steering.Update(23.5, 0.01), 0.5, "held tilt stays turned");
            steering.Inverted = true;
            Equal(steering.Update(45, 0.01), -1, "inversion");
            steering.Inverted = false;
            steering.Center(0, 0, -1);
            Equal(steering.Angle(0.5, 0, -Math.Sqrt(0.75)), 30, "horizontal posture");
            steering.Center(0.2, -Math.Sqrt(0.96), 0);
            Equal(steering.Angle(0.2, -Math.Sqrt(0.96), 0), 0, "offset recentering");
            steering.Reset();
            Equal(steering.Update(45, 0.01), 0, "disconnect reset");
            steering.Center(0, -1, 0);
            Equal(steering.Update(Double.NaN, 0.01), 0, "invalid input neutral");
            steering.SmoothingMilliseconds = 40;
            double smoothed = steering.Update(45, 0.01);
            if (smoothed <= 0 || smoothed >= 1) throw new Exception("smoothing failed");
            Console.WriteLine("PASS: neutral, held tilt, both directions, deadzone, limits, inversion, posture, recentering, reset, invalid input, smoothing.");
        }

        public static int Main()
        {
            try { Run(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        }
    }
}