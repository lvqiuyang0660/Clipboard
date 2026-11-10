using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipHelper
{
    /// <summary>Windows 原生能力：全局热键、剪贴板监听、粘贴按键、窗口定位。</summary>
    public static class Native
    {
        public const int WM_CLIPBOARDUPDATE = 0x031D;
        public const int WM_HOTKEY = 0x0312;
        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_NOREPEAT = 0x4000;
        public const int VK_V = 0x56;

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern IntPtr GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        public static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public INPUTUNION u; }

        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002;

        /// <summary>向当前前台窗口发送 Ctrl+V（直接粘贴）。</summary>
        public static void SendPaste()
        {
            INPUT[] inputs = new INPUT[4];
            inputs[0].type = INPUT_KEYBOARD; inputs[0].u.ki.wVk = 0x11; // Ctrl down
            inputs[1].type = INPUT_KEYBOARD; inputs[1].u.ki.wVk = VK_V; // V down
            inputs[2].type = INPUT_KEYBOARD; inputs[2].u.ki.wVk = VK_V; inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[3].type = INPUT_KEYBOARD; inputs[3].u.ki.wVk = 0x11; inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
            SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { }
        }

        public static string ClipboardFormat(int id)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClipboardFormatName(id, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClipboardFormatName(int format, StringBuilder lpszFormatName, int cchFormatName);
    }
}
