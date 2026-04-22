using System;
using System.Runtime.InteropServices;

namespace SimpleRemote.Shared
{
    internal static class DpiAwareness
    {
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
        private static readonly IntPtr PerMonitorAware = new IntPtr(-3);
        private static readonly IntPtr SystemAware = new IntPtr(-2);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiFlag);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        public static void Enable()
        {
            if (TrySetContext(PerMonitorAwareV2))
            {
                return;
            }

            if (TrySetContext(PerMonitorAware))
            {
                return;
            }

            if (TrySetContext(SystemAware))
            {
                return;
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        private static bool TrySetContext(IntPtr context)
        {
            try
            {
                return SetProcessDpiAwarenessContext(context);
            }
            catch
            {
                return false;
            }
        }
    }
}
