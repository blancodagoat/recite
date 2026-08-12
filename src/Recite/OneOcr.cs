using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Recite;

/// <summary>
/// The newer OCR model Windows 11 ships inside the Snipping Tool package (used by its
/// Text Actions) — markedly better than the legacy engine on small text and code-like
/// tokens. There is no public API: the DLL's C exports are loaded directly from the
/// user's installed package, and anything unexpected (package missing, exports renamed,
/// model refused) makes <see cref="Available"/> false so callers fall back to the legacy
/// engine. Nothing is bundled or downloaded.
/// </summary>
internal static unsafe class OneOcr
{
    private static readonly object Gate = new();
    private static bool initialized;
    private static long pipeline;
    private static long processOptions;

    // Every handle, count and index in this API is a 64-bit value. Declaring any of them
    // as int32 leaves garbage in the upper half of the argument register (or overflows an
    // output buffer), which the engine reports as a generic 0x3 — the bug that made the
    // recognition call fail while load and pipeline creation succeeded.
    private static delegate* unmanaged[Cdecl]<long*, long> createInitOptions;
    private static delegate* unmanaged[Cdecl]<long, byte, long> setDelayLoad;
    private static delegate* unmanaged[Cdecl]<byte*, byte*, long, long*, long> createPipeline;
    private static delegate* unmanaged[Cdecl]<long*, long> createProcessOptions;
    private static delegate* unmanaged[Cdecl]<long, long, long> setMaxLineCount;
    private static delegate* unmanaged[Cdecl]<long, RawImage*, long, long*, long> runPipeline;
    private static delegate* unmanaged[Cdecl]<long, long*, long> getLineCount;
    private static delegate* unmanaged[Cdecl]<long, long, long*, long> getLine;
    private static delegate* unmanaged[Cdecl]<long, byte**, long> getLineContent;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawImage
    {
        public int Type;
        public int Columns;
        public int Rows;
        public int Reserved;
        public long Step;
        public long Data;
    }

    /// <summary>
    /// On by default; the config flag turns it off to force the legacy engine. When the
    /// package is present the newer model reads small text and code tokens the legacy
    /// engine fumbles, and any failure (package absent, exports changed, recognition
    /// error) falls through silently, so there is no downside to leaving it on.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>BGRA pixel-format code in the image descriptor, calibrated against the
    /// engine (0 and 4/5 error; 1 and 3 both work; 3 is the canonical value).</summary>
    private const int ImageType = 3;

    /// <summary>Triggers staging and load ahead of the first grab. Call on a worker
    /// thread at startup so the one-time ~100 MB copy never blocks a grab.</summary>
    public static void WarmUp() => _ = Available;

    public static bool Available
    {
        get
        {
            if (!Enabled)
            {
                return false;
            }

            lock (Gate)
            {
                if (!initialized)
                {
                    initialized = true;
                    try
                    {
                        Initialize();
                    }
                    catch (Exception ex)
                    {
                        pipeline = 0;
                        AppLog.Write("OneOCR unavailable: " + ex.Message);
                    }
                }

                return pipeline != 0;
            }
        }
    }

    // The engine rejects images below roughly 50px in a dimension (returns 0x3), and a
    // one-line grab of UI text is often shorter, so anything smaller is padded onto a
    // white canvas that clears the floor with margin. Padding, not scaling — glyph sizes
    // stay exact, the white border is ignored by recognition.
    private const int MinDimension = 64;

    /// <summary>Recognized lines, top to bottom. Caller must have checked <see cref="Available"/>.</summary>
    public static List<string> Read(Bitmap bitmap)
    {
        Bitmap? padded = null;
        if (bitmap.Width < MinDimension || bitmap.Height < MinDimension)
        {
            padded = new Bitmap(Math.Max(bitmap.Width, MinDimension), Math.Max(bitmap.Height, MinDimension));
            using var g = Graphics.FromImage(padded);
            g.Clear(Color.White);
            g.DrawImageUnscaled(bitmap, 0, 0);
            bitmap = padded;
        }

        try
        {
            return ReadCore(bitmap);
        }
        finally
        {
            padded?.Dispose();
        }
    }

    private static List<string> ReadCore(Bitmap bitmap)
    {
        // BGRA rows straight out of the GDI bitmap; no encode/decode round trip.
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var image = new RawImage
            {
                Type = ImageType,
                Columns = bitmap.Width,
                Rows = bitmap.Height,
                Step = data.Stride,
                Data = data.Scan0.ToInt64(),
            };

            var lines = new List<string>();
            lock (Gate)
            {
                long instance;
                Check(runPipeline(pipeline, &image, processOptions, &instance));
                long count;
                Check(getLineCount(instance, &count));
                for (long i = 0; i < count; i++)
                {
                    long line;
                    if (getLine(instance, i, &line) != 0 || line == 0)
                    {
                        continue;
                    }

                    byte* content;
                    if (getLineContent(line, &content) == 0 && content != null)
                    {
                        var text = Marshal.PtrToStringUTF8((IntPtr)content);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            lines.Add(text);
                        }
                    }
                }
            }

            return lines;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void Initialize()
    {
        var source = FindEngineDirectory()
            ?? throw new InvalidOperationException("No installed package carries the engine (Snipping Tool and Photos both absent).");

        // The DLL cannot be loaded in place: WindowsApps denies LoadLibrary to outside
        // processes (E_ACCESSDENIED). Reading the files is allowed, so the engine is
        // staged into a writable cache and loaded from there. The stamp is the source
        // path, which carries the package version, so a Photos/Snipping update re-stages.
        var root = StageEngine(source);

        var dllPath = Path.Combine(root, "oneocr.dll");
        var modelPath = Path.Combine(root, "oneocr.onemodel");

        // Load the ONNX runtime first so the engine's implicit dependency resolves from
        // the cache rather than being searched for on the system path.
        var onnx = Path.Combine(root, "onnxruntime.dll");
        if (File.Exists(onnx))
        {
            NativeLibrary.Load(onnx);
        }

        var library = NativeLibrary.Load(dllPath);
        createInitOptions = (delegate* unmanaged[Cdecl]<long*, long>)Export(library, "CreateOcrInitOptions");
        setDelayLoad = (delegate* unmanaged[Cdecl]<long, byte, long>)Export(library, "OcrInitOptionsSetUseModelDelayLoad");
        createPipeline = (delegate* unmanaged[Cdecl]<byte*, byte*, long, long*, long>)Export(library, "CreateOcrPipeline");
        createProcessOptions = (delegate* unmanaged[Cdecl]<long*, long>)Export(library, "CreateOcrProcessOptions");
        setMaxLineCount = (delegate* unmanaged[Cdecl]<long, long, long>)Export(library, "OcrProcessOptionsSetMaxRecognitionLineCount");
        runPipeline = (delegate* unmanaged[Cdecl]<long, RawImage*, long, long*, long>)Export(library, "RunOcrPipeline");
        getLineCount = (delegate* unmanaged[Cdecl]<long, long*, long>)Export(library, "GetOcrLineCount");
        getLine = (delegate* unmanaged[Cdecl]<long, long, long*, long>)Export(library, "GetOcrLine");
        getLineContent = (delegate* unmanaged[Cdecl]<long, byte**, long>)Export(library, "GetOcrLineContent");

        long init;
        Check(createInitOptions(&init));
        Check(setDelayLoad(init, 0));  // load the model now, not lazily

        // The model refuses to load without this exact key; it is baked into the DLL and
        // documented by every open-source integration of this engine.
        var key = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4"u8.ToArray();
        var model = System.Text.Encoding.UTF8.GetBytes(modelPath + "\0");
        long created;
        fixed (byte* modelPtr = model)
        fixed (byte* keyPtr = key)
        {
            Check(createPipeline(modelPtr, keyPtr, init, &created));
        }

        long options;
        Check(createProcessOptions(&options));
        Check(setMaxLineCount(options, 1000));

        processOptions = options;
        pipeline = created;
        AppLog.Write("OneOCR engine loaded from " + root);
    }

    /// <summary>
    /// Copies the engine files out of the (unloadable) package directory into a writable
    /// cache, keyed by source path so a package update re-stages. Returns the cache dir.
    /// A complete prior copy is reused as-is.
    /// </summary>
    private static string StageEngine(string source)
    {
        string[] required = ["oneocr.dll", "oneocr.onemodel"];
        string[] optional = ["onnxruntime.dll", "oneocr_tensorrt_plugin.dll"];

        var cache = Path.Combine(AppInfo.DataDirectory, "oneocr");
        var stamp = Path.Combine(cache, "source.txt");
        bool current = File.Exists(stamp)
            && File.ReadAllText(stamp) == source
            && required.All(f => File.Exists(Path.Combine(cache, f)));

        if (!current)
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }

            Directory.CreateDirectory(cache);
            foreach (var file in required)
            {
                File.Copy(Path.Combine(source, file), Path.Combine(cache, file), overwrite: true);
            }

            foreach (var file in optional)
            {
                var from = Path.Combine(source, file);
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(cache, file), overwrite: true);
                }
            }

            File.WriteAllText(stamp, source);
        }

        return cache;
    }

    /// <summary>
    /// The engine rides inside several inbox packages; Snipping Tool is the usual host,
    /// Photos carries it too (which matters on debloated installs where Snipping Tool
    /// has been removed). First package that has both the DLL and its model wins.
    /// </summary>
    private static string? FindEngineDirectory()
    {
        string[] families =
        [
            "Microsoft.ScreenSketch_8wekyb3d8bbwe",
            "Microsoft.Windows.Photos_8wekyb3d8bbwe",
        ];

        foreach (var family in families)
        {
            try
            {
                var manager = new Windows.Management.Deployment.PackageManager();
                foreach (var package in manager.FindPackagesForUser(string.Empty, family))
                {
                    var root = package.InstalledLocation.Path;
                    foreach (var candidate in new[] { Path.Combine(root, "SnippingTool"), root })
                    {
                        if (File.Exists(Path.Combine(candidate, "oneocr.dll"))
                            && File.Exists(Path.Combine(candidate, "oneocr.onemodel")))
                        {
                            return candidate;
                        }
                    }
                }
            }
            catch
            {
                // Package enumeration can fail on stripped-down systems; try the next.
            }
        }

        return null;
    }

    private static IntPtr Export(IntPtr library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address)
            ? address
            : throw new InvalidOperationException($"oneocr.dll no longer exports {name}.");

    private static void Check(long status)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"OneOCR call failed with 0x{status:X}.");
        }
    }
}
