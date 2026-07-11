using System;
using System.Text;
using System.Runtime.InteropServices;

namespace WeChatSidekick
{
    public static class WindowAttachmentHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct WindowRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        public static bool IsForegroundProcess(params string[] processNames)
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                string current = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                foreach (string processName in processNames)
                {
                    if (string.Equals(current, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool TryGetWeChatWindowRect(out WindowRect rect)
        {
            rect = new WindowRect();
            IntPtr hwnd = FindWeChatWindow();
            if (hwnd == IntPtr.Zero) return false;
            return GetWindowRect(hwnd, out rect);
        }

        public static IntPtr FindWeChatWindow()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
                {
                    if (!IsWindowVisible(hwnd)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    string processName = "";
                    try { processName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                    catch { }
                    if (!string.Equals(processName, "Weixin", StringComparison.OrdinalIgnoreCase)) return true;

                    WindowRect r;
                    if (!GetWindowRect(hwnd, out r)) return true;
                    int width = r.Right - r.Left;
                    int height = r.Bottom - r.Top;
                    if (width < 300 || height < 300) return true;

                    StringBuilder title = new StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);
                    StringBuilder className = new StringBuilder(256);
                    GetClassName(hwnd, className, className.Capacity);

                    if (title.ToString() == "微信" ||
                        className.ToString().IndexOf("Qt", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = hwnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }
    }
}
