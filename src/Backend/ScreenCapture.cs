using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WeChatSidekick
{
    /// <summary>
    /// captures a region of the screen into a bitmap for pixel-level inspection.
    /// </summary>
    public class ScreenCapture : IDisposable
    {
        public Bitmap Bmp { get; private set; }
        public int ScreenX { get; private set; }
        public int ScreenY { get; private set; }

        private byte[] _pixels;
        private int _stride;
        private int _width;
        private int _height;

        [ThreadStatic]
        private static Bitmap _cachedBmp;
        
        [ThreadStatic]
        private static byte[] _cachedPixels;

        public ScreenCapture(int x, int y, int w, int h)
        {
            ScreenX = x;
            ScreenY = y;
            if (w > 0 && h > 0)
            {
                try
                {
                    if (_cachedBmp == null || _cachedBmp.Width != w || _cachedBmp.Height != h)
                    {
                        if (_cachedBmp != null) _cachedBmp.Dispose();
                        _cachedBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        _cachedPixels = null;
                    }
                    Bmp = _cachedBmp;

                    using (Graphics g = Graphics.FromImage(Bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
                    }

                    _width = w;
                    _height = h;

                    BitmapData bmpData = Bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, Bmp.PixelFormat);
                    _stride = Math.Abs(bmpData.Stride);
                    int bytes = _stride * h;
                    if (_cachedPixels == null || _cachedPixels.Length < bytes)
                    {
                        _cachedPixels = new byte[bytes];
                    }
                    _pixels = _cachedPixels;
                    Marshal.Copy(bmpData.Scan0, _pixels, 0, bytes);
                    Bmp.UnlockBits(bmpData);
                }
                catch
                {
                    Bmp = null;
                    _pixels = null;
                }
            }
        }

        public Color GetPixel(int screenX, int screenY)
        {
            if (_pixels == null) return Color.Black;
            int lx = screenX - ScreenX;
            int ly = screenY - ScreenY;
            if (lx >= 0 && lx < _width && ly >= 0 && ly < _height)
            {
                int index = ly * _stride + lx * 4;
                if (index >= 0 && index + 3 < _pixels.Length)
                {
                    byte b = _pixels[index];
                    byte g = _pixels[index + 1];
                    byte r = _pixels[index + 2];
                    byte a = _pixels[index + 3];
                    return Color.FromArgb(a, r, g, b);
                }
            }
            return Color.Black;
        }

        public void Dispose()
        {
            // do not dispose cached bitmap
        }
    }
}
