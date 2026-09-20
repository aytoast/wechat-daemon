using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WeChatSidekick.Backend
{
    // A bounded UIA viewport, not a claim about WeChat's absolute maximum size.
    public sealed class BackfillWindowScope : IDisposable
    {
        private readonly IntPtr _hwnd;
        private Win32Helper.WINDOWPLACEMENT _placement;
        private Win32Helper.RECT _original;
        public int ActualHeight { get; private set; }

        public BackfillWindowScope(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _placement.length = Marshal.SizeOf(typeof(Win32Helper.WINDOWPLACEMENT));
            if (!Win32Helper.GetWindowPlacement(hwnd, ref _placement) || !Win32Helper.GetWindowRect(hwnd, out _original))
                throw new InvalidOperationException("Could not save WeChat window placement.");
            try
            {
                var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
                int width = _original.Right - _original.Left;
                foreach (int height in new[] { 32000, 16000, 8000 })
                {
                    if (!Win32Helper.SetWindowPos(hwnd, IntPtr.Zero, _original.Left, screen.WorkingArea.Top, width, height, 0x0414)) continue;
                    Thread.Sleep(800);
                    Win32Helper.RECT actual;
                    if (Win32Helper.GetWindowRect(hwnd, out actual))
                    {
                        ActualHeight = actual.Bottom - actual.Top;
                        if (ActualHeight >= height) return;
                    }
                }
                throw new InvalidOperationException("WeChat rejected oversized viewport.");
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            Win32Helper.SetWindowPlacement(_hwnd, ref _placement);
            Win32Helper.SetWindowPos(_hwnd, IntPtr.Zero, _original.Left, _original.Top,
                _original.Right - _original.Left, _original.Bottom - _original.Top, 0x0454);
        }
    }
}
