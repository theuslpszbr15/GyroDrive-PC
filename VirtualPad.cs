using System;
using System.Runtime.InteropServices;

namespace GyroDrive
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XboxReport
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    public sealed class VirtualPad : IDisposable
    {
        private const uint Success = 0x20000000;
        private IntPtr client, target;
        private bool attached;
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr vigem_alloc();
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint vigem_connect(IntPtr client);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vigem_disconnect(IntPtr client);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vigem_free(IntPtr client);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr vigem_target_x360_alloc();
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint vigem_target_add(IntPtr client, IntPtr target);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint vigem_target_remove(IntPtr client, IntPtr target);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern void vigem_target_free(IntPtr target);
        [DllImport("ViGEmClient.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint vigem_target_x360_update(IntPtr client, IntPtr target, XboxReport report);

        public VirtualPad()
        {
            try
            {
                client = vigem_alloc();
                if (client == IntPtr.Zero) throw new Exception("Cliente virtual indisponivel");
                Check(vigem_connect(client));
                target = vigem_target_x360_alloc();
                if (target == IntPtr.Zero) throw new Exception("Controle virtual indisponivel");
                Check(vigem_target_add(client, target));
                attached = true;
            }
            catch { Dispose(); throw; }
        }

        private static void Check(uint result)
        {
            if (result != Success) throw new Exception("Saida virtual indisponivel (0x" + result.ToString("X8") + ")");
        }

        public static XboxReport Map(PadState state, double steering)
        {
            XboxReport report = new XboxReport();
            report.Buttons = (ushort)(state.Buttons & 0xF3FF);
            report.LeftTrigger = Trigger(state.LeftTrigger);
            report.RightTrigger = Trigger(state.RightTrigger);
            report.LeftX = Axis(steering);
            report.LeftY = Axis(state.LeftY);
            report.RightX = Axis(state.RightX);
            report.RightY = Axis(state.RightY);
            return report;
        }

        private static short Axis(double value)
        {
            if (!Steering.Finite(value)) return 0;
            value = Math.Max(-1, Math.Min(1, value));
            return (short)Math.Round(value * (value < 0 ? 32768 : 32767));
        }

        private static byte Trigger(double value)
        {
            return Steering.Finite(value) ? (byte)Math.Round(Math.Max(0, Math.Min(1, value)) * 255) : (byte)0;
        }

        public void Send(PadState state, double steering)
        {
            if (!attached) return;
            Check(vigem_target_x360_update(client, target, Map(state, steering)));
        }

        public void Neutral()
        {
            if (attached) vigem_target_x360_update(client, target, new XboxReport());
        }

        public static bool DriverAvailable()
        {
            IntPtr probe = IntPtr.Zero;
            try
            {
                probe = vigem_alloc();
                return probe != IntPtr.Zero && vigem_connect(probe) == Success;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            finally
            {
                if (probe != IntPtr.Zero) { vigem_disconnect(probe); vigem_free(probe); }
            }
        }

        public void Dispose()
        {
            if (attached)
            {
                Neutral();
                vigem_target_remove(client, target);
                attached = false;
            }
            if (target != IntPtr.Zero) { vigem_target_free(target); target = IntPtr.Zero; }
            if (client != IntPtr.Zero) { vigem_disconnect(client); vigem_free(client); client = IntPtr.Zero; }
        }

        public static void Tests()
        {
            if (Marshal.SizeOf(typeof(XboxReport)) != 12) throw new Exception("Xbox report layout mismatch");
            XboxReport neutral = Map(new PadState(), 0);
            if (neutral.Buttons != 0 || neutral.LeftX != 0 || neutral.RightTrigger != 0) throw new Exception("Neutral mapping failed");
            PadState state = new PadState { Buttons = 4096 | 256, RightTrigger = 1, LeftTrigger = 0.5f, RightX = -1 };
            XboxReport report = Map(state, 1);
            if (report.Buttons != 4352 || report.RightTrigger != 255 || report.LeftTrigger != 128 || report.LeftX != 32767 || report.RightX != -32768)
                throw new Exception("Xbox mapping failed");
            if (Map(state, -2).LeftX != -32768 || Map(state, Double.NaN).LeftX != 0) throw new Exception("Axis safety failed");
            Console.WriteLine("PASS: Xbox report layout, buttons, triggers, axes, neutral and clamping.");
        }
    }
}