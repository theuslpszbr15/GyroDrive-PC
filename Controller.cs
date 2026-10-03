using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GyroDrive
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PadState
    {
        public int Buttons;
        public float LeftTrigger, RightTrigger, LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ImuState
    {
        public float AccelX, AccelY, AccelZ, GyroX, GyroY, GyroZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MotionState
    {
        public float QuatW, QuatX, QuatY, QuatZ, AccelX, AccelY, AccelZ, GravityX, GravityY, GravityZ;
    }

    public static class Controller
    {
        private const string Library = "JoyShockLibrary.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int JslConnectDevices();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int JslGetConnectedDeviceHandles([Out] int[] handles, int size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern int JslGetControllerType(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool JslStillConnected(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern float JslGetTimeSinceLastUpdate(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern PadState JslGetSimpleState(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern ImuState JslGetIMUState(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern MotionState JslGetMotionState(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void JslSetAutomaticCalibration(int device, [MarshalAs(UnmanagedType.I1)] bool enabled);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void JslResetContinuousCalibration(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void JslStartContinuousCalibration(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void JslPauseContinuousCalibration(int device);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] public static extern void JslDisconnectAndDisposeAll();

        public static int Find()
        {
            int count = JslConnectDevices();
            int[] handles = new int[Math.Max(1, count)];
            int found = JslGetConnectedDeviceHandles(handles, handles.Length);
            int candidate = -1;
            for (int index = 0; index < found; index++)
            {
                int handle = handles[index];
                if (JslGetControllerType(handle) != 4 || !JslStillConnected(handle)) continue;
                if (Fresh(handle)) return handle;
                if (candidate < 0) candidate = handle;
            }
            return candidate;
        }

        public static bool Fresh(int device)
        {
            if (device < 0 || !JslStillConnected(device)) return false;
            double age = JslGetTimeSinceLastUpdate(device);
            return Steering.Finite(age) && age >= 0 && age < 0.25;
        }

        public static bool NeedsReconnect(bool connected, double secondsWithoutData)
        {
            return !connected || !Steering.Finite(secondsWithoutData) || secondsWithoutData >= 1.5;
        }

        public static bool NeedsCalibrationRestart(double gyroSpeed, double accelerationChange)
        {
            return !Steering.Finite(gyroSpeed) || !Steering.Finite(accelerationChange) || accelerationChange > 0.04;
        }

        public static void Tests()
        {
            if (NeedsReconnect(true, 0.1) || NeedsReconnect(true, 1.49) ||
                !NeedsReconnect(true, 1.5) || !NeedsReconnect(false, 0) ||
                !NeedsReconnect(true, Double.NaN))
                throw new Exception("Reconnect policy failed");
            Console.WriteLine("PASS: reconnect on disconnected or stale device, tolerance for short signal gaps.");
            if (NeedsCalibrationRestart(287, 0.001) || NeedsCalibrationRestart(14, 0.04) ||
                !NeedsCalibrationRestart(0, 0.041) || !NeedsCalibrationRestart(Double.NaN, 0) ||
                !NeedsCalibrationRestart(0, Double.NaN))
                throw new Exception("Calibration restart policy failed");
            Console.WriteLine("PASS: stable high gyro bias does not continuously restart calibration; movement and invalid samples do.");
        }

        public static int Probe()
        {
            try
            {
                if (Marshal.SizeOf(typeof(PadState)) != 28 || Marshal.SizeOf(typeof(ImuState)) != 24 || Marshal.SizeOf(typeof(MotionState)) != 40)
                    throw new Exception("Native structure size mismatch.");
                int device = Find();
                if (device < 0) throw new Exception("No DualShock 4 detected by the sensor library.");
                JslSetAutomaticCalibration(device, false);
                JslResetContinuousCalibration(device);
                JslStartContinuousCalibration(device);
                Stopwatch acquisition = Stopwatch.StartNew();
                int readings = 0;
                long nextReading = 0;
                MotionState motion = new MotionState();
                ImuState imu = new ImuState();
                while (acquisition.ElapsedMilliseconds < 1500)
                {
                    if (acquisition.ElapsedMilliseconds >= nextReading && Fresh(device))
                    {
                        nextReading = acquisition.ElapsedMilliseconds + 200;
                        motion = JslGetMotionState(device);
                        imu = JslGetIMUState(device);
                        readings++;
                        Console.WriteLine("SAMPLE: accel=({0:F3}, {1:F3}, {2:F3}); gyro=({3:F3}, {4:F3}, {5:F3})", imu.AccelX, imu.AccelY, imu.AccelZ, imu.GyroX, imu.GyroY, imu.GyroZ);
                    }
                    System.Threading.Thread.Yield();
                }
                JslPauseContinuousCalibration(device);
                double magnitude = Math.Sqrt(motion.GravityX * motion.GravityX + motion.GravityY * motion.GravityY + motion.GravityZ * motion.GravityZ);
                if (readings == 0 || !Steering.Finite(magnitude) || magnitude < 0.5 || magnitude > 1.5)
                    throw new Exception("No fresh, valid gravity data received.");
                Console.WriteLine("PASS: DualShock 4 handle={0}; fresh sensor readings={1}; gravity=({2:F4}, {3:F4}, {4:F4}); magnitude={5:F4}; gyro=({6:F3}, {7:F3}, {8:F3}); age={9:F4}s", device, readings, motion.GravityX, motion.GravityY, motion.GravityZ, magnitude, imu.GyroX, imu.GyroY, imu.GyroZ, JslGetTimeSinceLastUpdate(device));
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
            finally { JslDisconnectAndDisposeAll(); }
        }
    }

    public static class SensorProbe
    {
        public static int Main() { return Controller.Probe(); }
    }
}