using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExileEye.Core;

public sealed class Settings
{
    /// <summary>PoE2 client language: "en" or "ru". Drives the OCR model and price-name aliases.</summary>
    public string Language { get; set; } = "en";

    /// <summary>poe.ninja league name, used verbatim as the API parameter.</summary>
    public string League { get; set; } = "Runes of Aldur";

    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }

    /// <summary>Horizontal gap (px) between the calibrated region's right edge and the price labels.</summary>
    public int OverlayGap { get; set; } = 8;

    [JsonIgnore]
    public Rectangle Region
    {
        get => new(RegionX, RegionY, RegionWidth, RegionHeight);
        set { RegionX = value.X; RegionY = value.Y; RegionWidth = value.Width; RegionHeight = value.Height; }
    }

    [JsonIgnore]
    public bool IsCalibrated => RegionWidth > 0 && RegionHeight > 0;

    public static readonly string[] Leagues = ["Runes of Aldur", "HC Runes of Aldur"];
    public static readonly (string Code, string Label)[] Languages = [("en", "English"), ("ru", "Русский")];

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt settings → start fresh rather than crash at boot */ }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch (Exception ex) { Console.Error.WriteLine($"[Settings] save failed: {ex.Message}"); }
    }
}
