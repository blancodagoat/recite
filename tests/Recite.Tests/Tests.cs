// Assertions over Recite's logic, including a real OCR round trip: a known sentence is
// rendered into a bitmap in-process and the Windows OCR engine must read it back. No
// desktop needed, so everything runs headless, including on CI.
//
//   dotnet run --project tests/Recite.Tests
//
// Exit code is 0 when everything passes.

using System.Drawing.Text;
using Recite;

int failed = 0, passed = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; return; }
    failed++;
    Console.WriteLine($"FAIL  {name}{(detail is null ? "" : "  -> " + detail)}");
}

void Eq(string name, object? actual, object? expected) =>
    Check(name, Equals(actual, expected), $"expected <{expected}>, got <{actual}>");

// HotkeyBinding
Eq("default grab renders", HotkeyBinding.DefaultGrab.ToString(), "Ctrl+PrintScreen");
Check("default grab parses back",
    HotkeyBinding.TryParse("Ctrl+PrintScreen", out var parsed) && parsed == HotkeyBinding.DefaultGrab);
Check("modifier-only rejected", !HotkeyBinding.TryParse("Ctrl+Shift", out _));

// Line joining
Eq("lines trim and join", Ocr.JoinLines(["  hello ", "world  "]), $"hello{Environment.NewLine}world");
Eq("empty input joins to empty", Ocr.JoinLines([]), "");
Eq("outer blanks trimmed", Ocr.JoinLines(["", "text", ""]).Trim(), "text");

// Token repair: the engine's O/0 and l/1 confusions inside code-like tokens
Eq("hex prefix repaired", Ocr.RepairTokens("error Ox80070005 denied"), "error 0x80070005 denied");
Eq("zero inside digits repaired", Ocr.RepairTokens("port 8O80"), "port 8080");
Eq("one inside digits repaired", Ocr.RepairTokens("v1.l0.3"), "v1.10.3");
Eq("prose untouched", Ocr.RepairTokens("Only Ill Officers"), "Only Ill Officers");
Eq("IPv4 untouched", Ocr.RepairTokens("IPv4 address"), "IPv4 address");

// Update check parsing
{
    const string releases = """
    [
      {"tag_name":"latest","prerelease":false,"html_url":"https://example/rolling"},
      {"tag_name":"v1.3.0","prerelease":false,"html_url":"https://example/v130"}
    ]
    """;
    Check("update check finds newer stable",
        UpdateCheck.ParseNewest(releases, new Version(1, 0, 0)) is { } n && n.Version == new Version(1, 3, 0));
    Check("update check ignores rolling tag",
        UpdateCheck.ParseNewest(releases, new Version(1, 3, 0)) is null);
}

// Live OCR round trip: render a sentence, read it back. Skips (without failing) only if
// this Windows has no OCR language pack at all.
{
    const string sentence = "The quick brown fox jumps over the lazy dog";
    using var bitmap = new Bitmap(900, 120);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var font = new Font("Segoe UI", 24f);
        g.DrawString(sentence, font, Brushes.Black, 20, 35);
    }

    try
    {
        Check("OneOCR is off by default", !OneOcr.Available);
        OneOcr.Enabled = true;
        Console.WriteLine($"engine when opted in: {(OneOcr.Available ? "OneOCR (staged from package)" : "legacy (OneOCR not ready on this box)")}");
        OneOcr.Enabled = false;

        string text = await Ocr.Read(bitmap);
        Check("ocr reads rendered text", text.Contains("quick brown fox", StringComparison.OrdinalIgnoreCase), text);

        // Both engines stay covered: the same sentence must also survive the legacy path.
        Ocr.ForceLegacy = true;
        string legacy = await Ocr.Read(bitmap);
        Ocr.ForceLegacy = false;
        Check("legacy engine also reads it", legacy.Contains("quick brown fox", StringComparison.OrdinalIgnoreCase), legacy);

        // The hard case: small 10pt UI text with a hex token, which needs both the
        // upscale pass (else the token drops) and token repair (the engine reads the
        // leading zero as a capital O below 16pt). 8pt and below is genuinely outside
        // the engine's reliable envelope, upscaled or not, on any tool built on it.
        using var small = new Bitmap(360, 44);
        using (var g = Graphics.FromImage(small))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            using var font = new Font("Segoe UI", 10f);
            g.DrawString("error code 0x80070005 access denied", font, Brushes.Black, 4, 12);
        }

        string tiny = await Ocr.Read(small);
        Check("ocr reads small text via upscale and repair", tiny.Contains("0x80070005"), tiny);
    }
    catch (InvalidOperationException)
    {
        Console.WriteLine("SKIP ocr: no OCR language pack installed on this Windows.");
    }
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
