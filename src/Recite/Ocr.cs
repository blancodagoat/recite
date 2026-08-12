using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Recite;

/// <summary>
/// Text recognition through the OCR engine that ships inside Windows 10/11 — no bundled
/// models, no network, works offline in whatever languages the user has installed.
/// </summary>
internal static class Ocr
{
    /// <summary>Test hook: forces the legacy engine so both paths stay covered.</summary>
    internal static bool ForceLegacy;

    /// <summary>Recognizes text in the bitmap. Empty string when nothing was found.</summary>
    public static async Task<string> Read(Bitmap bitmap)
    {
        // The newer model from the Snipping Tool package wins when it's present and
        // healthy; it reads small text and code tokens the legacy engine fumbles. Any
        // failure inside it falls straight through to the legacy path.
        if (!ForceLegacy && OneOcr.Available)
        {
            try
            {
                // Recognition is synchronous CPU work; keep it off the UI thread.
                return await Task.Run(() => JoinLines(OneOcr.Read(bitmap)));
            }
            catch (Exception ex)
            {
                AppLog.Write("OneOCR read failed, using legacy engine: " + ex.Message);
            }
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? throw new InvalidOperationException(
                "Windows has no OCR language pack installed. Add one under Settings > Time & language > Language.");

        // Two scaling rules, both about the engine's sweet spot. Oversized grabs are
        // downscaled to its hard input limit. Small grabs are upscaled: the engine
        // degrades on text below roughly 12 rendered pixels, and magnifying the region
        // first is the standard fix every serious tool on this engine uses.
        Bitmap? scaled = null;
        try
        {
            var source = bitmap;
            int limit = (int)OcrEngine.MaxImageDimension;
            int longest = Math.Max(bitmap.Width, bitmap.Height);
            double factor = longest > limit ? (double)limit / longest
                : longest < 400 ? Math.Min(4.0, (double)limit / longest)
                : longest < 900 ? Math.Min(2.0, (double)limit / longest)
                : 1.0;
            if (factor != 1.0)
            {
                int w = Math.Max(1, (int)(bitmap.Width * factor));
                int h = Math.Max(1, (int)(bitmap.Height * factor));
                scaled = new Bitmap(w, h);
                using (var g = Graphics.FromImage(scaled))
                {
                    // Bicubic, not the default bilinear: glyph edges survive the resize,
                    // which is the difference between reading "0x80070005" and losing it.
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(bitmap, new Rectangle(0, 0, w, h),
                        new Rectangle(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel);
                }

                source = scaled;
            }

            using var softwareBitmap = await ToSoftwareBitmap(source);
            var result = await engine.RecognizeAsync(softwareBitmap);
            return JoinLines(result.Lines.Select(l => l.Text));
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>Trimmed lines joined with newlines; leading and trailing blanks dropped.</summary>
    public static string JoinLines(IEnumerable<string> lines) =>
        string.Join(Environment.NewLine, lines.Select(l => RepairTokens(l.Trim()))).Trim();

    /// <summary>
    /// Screen text is full of code-like tokens — error codes, hashes, versions — and the
    /// engine's classic small-size confusions (O for 0, l/I for 1) turn them into strings
    /// that won't google. Repairs are deliberately narrow: only inside tokens that carry
    /// digits, and only where the confused character touches a digit, so prose and
    /// identifiers like IPv4 pass through untouched.
    /// </summary>
    public static string RepairTokens(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"\S+", match =>
        {
            var token = match.Value;
            if (!token.Any(char.IsDigit))
            {
                return token;
            }

            // "Ox" ahead of hex digits is the zero the engine misread.
            token = System.Text.RegularExpressions.Regex.Replace(
                token, @"^[Oo]x(?=[0-9A-Fa-f])", "0x");

            var chars = token.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                bool digitBefore = i > 0 && char.IsDigit(chars[i - 1]);
                bool digitAfter = i + 1 < chars.Length && char.IsDigit(chars[i + 1]);
                if (!digitBefore && !digitAfter)
                {
                    continue;
                }

                chars[i] = chars[i] switch
                {
                    'O' => '0',
                    'l' or 'I' => '1',
                    _ => chars[i],
                };
            }

            return new string(chars);
        });

    private static async Task<SoftwareBitmap> ToSoftwareBitmap(Bitmap bitmap)
    {
        byte[] png;
        using (var buffer = new MemoryStream())
        {
            bitmap.Save(buffer, ImageFormat.Png);
            png = buffer.ToArray();
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(System.Runtime.InteropServices.WindowsRuntime
            .WindowsRuntimeBufferExtensions.AsBuffer(png));
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
