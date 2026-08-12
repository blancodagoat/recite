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
    /// <summary>Recognizes text in the bitmap. Empty string when nothing was found.</summary>
    public static async Task<string> Read(Bitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? throw new InvalidOperationException(
                "Windows has no OCR language pack installed. Add one under Settings > Time & language > Language.");

        // The engine rejects images wider or taller than its limit; a proportional
        // downscale keeps huge multi-monitor grabs working at slightly lower fidelity.
        Bitmap? scaled = null;
        try
        {
            var source = bitmap;
            int limit = (int)OcrEngine.MaxImageDimension;
            int longest = Math.Max(bitmap.Width, bitmap.Height);
            if (longest > limit)
            {
                double factor = (double)limit / longest;
                scaled = new Bitmap(bitmap,
                    Math.Max(1, (int)(bitmap.Width * factor)),
                    Math.Max(1, (int)(bitmap.Height * factor)));
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
        string.Join(Environment.NewLine, lines.Select(l => l.Trim())).Trim();

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
