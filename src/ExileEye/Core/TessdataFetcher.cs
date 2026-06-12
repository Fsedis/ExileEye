using System.IO;
using System.Net.Http;

namespace ExileEye.Core;

/// <summary>
/// English OCR data ships inside the build (Tesseract.Data.English package); Russian has no
/// package, so rus.traineddata comes from the official tessdata_fast repo on first use
/// (~4 MB, LSTM-only — OcrReader opens it with EngineMode.LstmOnly).
/// </summary>
public static class TessdataFetcher
{
    private const string RussianUrl =
        "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/rus.traineddata";

    public static string TessdataDir => Path.Combine(AppContext.BaseDirectory, "tessdata");

    public static bool HasLanguage(string code) =>
        code != "ru" || File.Exists(Path.Combine(TessdataDir, "rus.traineddata"));

    /// <summary>Idempotent. Downloads to a temp name and renames so an interrupted download
    /// can't leave a truncated model that crashes the OCR engine on every later launch.</summary>
    public static async Task<bool> EnsureAsync(string code, HttpClient http)
    {
        if (HasLanguage(code)) return true;
        var target = Path.Combine(TessdataDir, "rus.traineddata");
        var partial = target + ".partial";
        try
        {
            Directory.CreateDirectory(TessdataDir);
            using (var resp = await http.GetAsync(RussianUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var file = File.Create(partial);
                await resp.Content.CopyToAsync(file);
            }
            File.Move(partial, target, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Tessdata] download failed: {ex.Message}");
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            return false;
        }
    }
}
