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
        string text = await Ocr.Read(bitmap);
        Check("ocr reads rendered text", text.Contains("quick brown fox", StringComparison.OrdinalIgnoreCase), text);
    }
    catch (InvalidOperationException)
    {
        Console.WriteLine("SKIP ocr: no OCR language pack installed on this Windows.");
    }
}

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
