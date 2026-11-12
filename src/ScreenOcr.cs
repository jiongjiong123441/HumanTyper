using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using static HumanTyper.NativeMethods;

namespace HumanTyper
{
    /// <summary>
    /// Last-resort proofreading source: a screenshot of the target window read with the OCR
    /// engine built into Windows 10/11 (Windows.Media.Ocr). Best at English.
    /// </summary>
    internal static class ScreenOcr
    {
        /// <summary>The two zoom levels every image is read at; a fix needs both to agree.</summary>
        public static readonly double[] Scales = { 2.0, 3.0 };

        /// <summary>
        /// Above this many enlarged pixels (both readings together) the two readings run one after
        /// the other instead of at the same time, to keep memory and CPU use reasonable.
        /// </summary>
        private const double ParallelPixelLimit = 12e6;

        private static bool? _available;

        /// <summary>A fresh engine per reading, so readings can run on several threads at once.</summary>
        private static OcrEngine NewEngine()
        {
            try
            {
                var en = new Windows.Globalization.Language("en-US");
                var e = OcrEngine.IsLanguageSupported(en) ? OcrEngine.TryCreateFromLanguage(en) : null;
                return e ?? OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { return null; }
        }

        public static bool Available => (_available ?? (_available = NewEngine() != null)).Value;

        internal sealed class Reading
        {
            public List<string> Words = new List<string>();
            public double LineHeight;          // average text line height, in source pixels
        }

        /// <summary>Reads an image at every zoom level — at the same time if the image is small enough.</summary>
        public static Reading[] ReadAll(Bitmap shot)
        {
            var res = new Reading[Scales.Length];
            double px = 0;
            foreach (var s in Scales) px += shot.Width * s * shot.Height * s;
            if (px <= ParallelPixelLimit)
            {
                // GDI+ bitmaps can't be drawn from two threads at once: give each reading its own copy.
                var copies = new Bitmap[Scales.Length];
                for (int i = 0; i < Scales.Length; i++) copies[i] = (Bitmap)shot.Clone();
                try { Parallel.For(0, Scales.Length, i => res[i] = Read(copies[i], Scales[i])); }
                finally { foreach (var c in copies) c.Dispose(); }
            }
            else
            {
                for (int i = 0; i < Scales.Length; i++) res[i] = Read(shot, Scales[i]);
            }
            return res;
        }

        /// <summary>OCRs an image, enlarged by scale first (small screen fonts read much better enlarged).</summary>
        internal static Reading Read(Bitmap shot, double scale)
        {
            var engine = NewEngine();
            if (engine == null) return null;
            try
            {
                double max = OcrEngine.MaxImageDimension;
                scale = Math.Min(scale, Math.Min(max / shot.Width, max / shot.Height));
                int w = Math.Max(1, (int)(shot.Width * scale)), h = Math.Max(1, (int)(shot.Height * scale));
                SoftwareBitmap sb;
                using (var big = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(shot, new Rectangle(0, 0, w, h));
                    }
                    sb = ToSoftwareBitmap(big);
                }
                using (sb)
                {
                    var result = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
                    var r = new Reading();
                    double hs = 0; int hn = 0;
                    foreach (var line in result.Lines)
                    {
                        foreach (var word in line.Words)
                        {
                            r.Words.Add(word.Text);
                            hs += word.BoundingRect.Height; hn++;
                        }
                    }
                    r.LineHeight = hn > 0 ? hs / hn / scale * 1.35 : 0;   // glyph height → line pitch
                    return r;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Grabs an area of the screen (screen pixels).</summary>
        public static Bitmap Capture(Rectangle area, bool printWindow, IntPtr hwnd)
        {
            if (area.Width < 8 || area.Height < 8) return null;
            var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    if (printWindow && GetWindowRect(hwnd, out RECT wr))
                    {
                        // Render just the target window (something of ours is covering it).
                        using (var whole = new Bitmap(Math.Max(1, wr.Right - wr.Left), Math.Max(1, wr.Bottom - wr.Top), PixelFormat.Format32bppArgb))
                        {
                            using (var gw = Graphics.FromImage(whole))
                            {
                                IntPtr hdc = gw.GetHdc();
                                bool ok = PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                                gw.ReleaseHdc(hdc);
                                if (!ok) { bmp.Dispose(); return null; }
                            }
                            g.DrawImage(whole, new Rectangle(0, 0, area.Width, area.Height),
                                new Rectangle(area.Left - wr.Left, area.Top - wr.Top, area.Width, area.Height), GraphicsUnit.Pixel);
                        }
                    }
                    else
                    {
                        g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    }
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                return null;
            }
        }

        /// <summary>True if two screenshots are (nearly) identical — scrolling didn't move anything.</summary>
        public static bool Same(Bitmap a, Bitmap b)
        {
            if (a == null || b == null || a.Size != b.Size) return false;
            var rect = new Rectangle(Point.Empty, a.Size);
            var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int n = da.Stride * a.Height;
                var ba = new byte[n]; var bb = new byte[n];
                Marshal.Copy(da.Scan0, ba, 0, n);
                Marshal.Copy(db.Scan0, bb, 0, n);
                int diff = 0;
                for (int i = 0; i < n; i += 4)
                    if (ba[i] != bb[i] || ba[i + 1] != bb[i + 1] || ba[i + 2] != bb[i + 2]) diff++;
                return diff < n / 4 / 1000;   // < 0.1 % of pixels (a blinking caret)
            }
            finally
            {
                a.UnlockBits(da);
                b.UnlockBits(db);
            }
        }

        private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;
                var ras = ms.AsRandomAccessStream();
                var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
                return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                              .AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
