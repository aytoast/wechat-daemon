using System;
using System.Text;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WeChatSidekick
{
    /// <summary>
    /// win32 interop helpers for window management and message classification.
    /// </summary>
    public static class Win32Helper
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>
        /// finds the window handle for the active WeChat process.
        /// </summary>
        public static IntPtr FindWeChatWindow()
        {
            IntPtr fallback = IntPtr.Zero;
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
                    if (processName != "Weixin") return true;

                    RECT rect;
                    GetWindowRect(hwnd, out rect);
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    if (width < 300 || height < 300) return true;

                    StringBuilder title = new StringBuilder(256);
                    GetWindowText(hwnd, title, title.Capacity);
                    StringBuilder className = new StringBuilder(256);
                    GetClassName(hwnd, className, className.Capacity);

                    if (fallback == IntPtr.Zero) fallback = hwnd;
                    if (title.ToString() == "微信" || className.ToString().IndexOf("Qt", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        fallback = hwnd;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return fallback;
        }

        /// <summary>
        /// checks if the active keyboard focus is within the WeChat input text box.
        /// </summary>
        public static bool IsWeChatTextBoxFocused()
        {
            try
            {
                IntPtr hwnd = FindWeChatWindow();
                if (hwnd == IntPtr.Zero) return false;

                uint wechatPid;
                GetWindowThreadProcessId(hwnd, out wechatPid);

                var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                if (focused == null) return false;

                if ((uint)focused.Current.ProcessId == wechatPid)
                {
                    var controlType = focused.Current.ControlType;
                    if (controlType == System.Windows.Automation.ControlType.Edit || 
                        controlType == System.Windows.Automation.ControlType.Document)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static bool IsForegroundWeChatOrSidekick()
        {
            return WindowAttachmentHelper.IsForegroundProcess("Weixin", "sidekick");
        }

        public static bool IsWeChatWindow(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return false;

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                string processName = "";
                try { processName = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
                catch { }
                if (processName != "Weixin") return false;

                RECT rect;
                GetWindowRect(hwnd, out rect);
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if (width < 300 || height < 300) return false;

                StringBuilder title = new StringBuilder(256);
                GetWindowText(hwnd, title, title.Capacity);
                StringBuilder className = new StringBuilder(256);
                GetClassName(hwnd, className, className.Capacity);

                return title.ToString() == "微信" ||
                       className.ToString().IndexOf("Qt", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// determines if a message bubble is from me by scanning for a contiguous green pixel run
        /// in the right-side padding zone. returns null if the item center is outside the capture area.
        /// </summary>
        public static bool? IsMessageFromMe(ScreenCapture cap, RECT rect)
        {
            if (cap == null || cap.Bmp == null) return null;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) return null;

            int centerY = rect.Top + h / 2;
            if (centerY < cap.ScreenY || centerY >= cap.ScreenY + cap.Bmp.Height)
            {
                return null;
            }

            return ScanForGreenBubble(w, h, rect.Top, rect.Left, cap);
        }

        /// <summary>
        /// scans horizontal strips of the message item for a contiguous run of green pixels
        /// indicating a WeChat "sent by me" bubble background.
        /// </summary>
        private static bool ScanForGreenBubble(int w, int h, int itemTop, int itemLeft, ScreenCapture cap)
        {
            int[] yOffsets = new int[] { h / 4, h / 2, (3 * h) / 4 };
            int startX = Math.Max(0, w - Constants.BubbleScanStartOffset);
            int endX = Math.Max(0, w - Constants.BubbleScanEndOffset);

            if (startX >= endX) return false;

            foreach (int yOffset in yOffsets)
            {
                int screenY = itemTop + yOffset;
                if (screenY < cap.ScreenY || screenY >= cap.ScreenY + cap.Bmp.Height)
                {
                    continue;
                }

                int run = 0;
                for (int xOffset = startX; xOffset < endX; xOffset++)
                {
                    int screenX = itemLeft + xOffset;
                    Color c = cap.GetPixel(screenX, screenY);
                    if (IsGreenBubblePixel(c))
                    {
                        run++;
                        if (run >= Constants.GreenRunThreshold) return true;
                    }
                    else
                    {
                        run = 0;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// checks if a pixel color matches the WeChat green bubble background.
        /// </summary>
        private static bool IsGreenBubblePixel(Color c)
        {
            return c.G > c.R + Constants.GreenChannelMinDelta
                && c.G > c.B - 10
                && c.G > Constants.GreenChannelMin
                && c.B > Constants.BlueChannelMin;
        }
    }
}
