using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SimpleRemote.Shared;

namespace SimpleRemote.Host
{
    internal static class InputInjector
    {
        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;

        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint MouseEventMiddleDown = 0x0020;
        private const uint MouseEventMiddleUp = 0x0040;
        private const uint MouseEventWheel = 0x0800;

        private const uint KeyEventKeyUp = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        public static void MouseMove(int x, int y)
        {
            var bounds = SystemInformation.VirtualScreen;
            var safeX = Clamp(bounds.Left + x, bounds.Left, bounds.Right - 1);
            var safeY = Clamp(bounds.Top + y, bounds.Top, bounds.Bottom - 1);
            SetCursorPos(safeX, safeY);
        }

        public static void MouseButton(MouseButtonCode button, bool isDown)
        {
            uint flags;

            switch (button)
            {
                case MouseButtonCode.Right:
                    flags = isDown ? MouseEventRightDown : MouseEventRightUp;
                    break;
                case MouseButtonCode.Middle:
                    flags = isDown ? MouseEventMiddleDown : MouseEventMiddleUp;
                    break;
                default:
                    flags = isDown ? MouseEventLeftDown : MouseEventLeftUp;
                    break;
            }

            var input = new INPUT
            {
                type = InputMouse,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = flags
                    }
                }
            };

            Send(input);
        }

        public static void MouseWheel(int delta)
        {
            var input = new INPUT
            {
                type = InputMouse,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = unchecked((uint)delta),
                        dwFlags = MouseEventWheel
                    }
                }
            };

            Send(input);
        }

        public static void Key(int virtualKey, bool isDown)
        {
            var input = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)virtualKey,
                        dwFlags = isDown ? 0u : KeyEventKeyUp
                    }
                }
            };

            Send(input);
        }

        private static void Send(INPUT input)
        {
            var inputs = new[] { input };
            SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
