using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
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
        private static OcrEngine _engine;
        private static bool _tried;

        private static OcrEngine Engine
        {
            get
            {
                if (_tried) return _engine;
                _tried = true;
                try
                {
                    var en = new Windows.Globalization.Language("en-US");
                    _engine = OcrEngine.IsLanguageSupported(en) ? OcrEngine.TryCreateFromLanguage(en) : null;
                    if (_engine == null) _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                }
                catch { _engine = null; }
                return _engine;
            }
        }

        public static bool Available => Engine != null;

        /// <summary>Captures the area of the screen (screen pixels) and returns the recognised words in reading order.</summary>
        public static List<string> ReadWords(Rectangle area, double scale, bool printWindow, IntPtr hwnd)
        {
            if (Engine == null || area.Width < 8 || area.Height < 8) return null;
            try
            {
                using (var shot = Capture(area, printWindow, hwnd))
                    return shot == null ? null : ReadWords(shot, scale);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>OCRs an image, enlarged by scale first (small screen fonts read much better enlarged).</summary>
        internal static List<string> ReadWords(Bitmap shot, double scale)
        {
            var engine = Engine;
            if (engine == null) return null;
            try
            {
                {
                    double max = OcrEngine.MaxImageDimension;
                    scale = Math.Min(scale, Math.Min(max / shot.Width, max / shot.Height));
                    int w = Math.Max(1, (int)(shot.Width * scale)), h = Math.Max(1, (int)(shot.Height * scale));
                    using (var big = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(big))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(shot, new Rectangle(0, 0, w, h));
                        }
                        var sb = ToSoftwareBitmap(big);
                        var result = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
                        var words = new List<string>();
                        foreach (var line in result.Lines)
                            foreach (var word in line.Words)
                                words.Add(word.Text);
                        return words;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap Capture(Rectangle area, bool printWindow, IntPtr hwnd)
        {
            var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
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
