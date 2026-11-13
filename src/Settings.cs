using System.Text.Json;

namespace HNY;

/// <summary>Настройки из меню трея. Хранятся в %APPDATA%\HNY\settings.json.</summary>
internal sealed class Settings
{
    public int Intensity { get; set; } = 1;
    public bool Drifts { get; set; } = true;
    public bool Snowballs { get; set; } = true;
    public bool Garland { get; set; } = true;
    public bool Plow { get; set; } = true;
    public bool Music { get; set; } = true;
    /// <summary>Громкость музыки в процентах (0..100). По умолчанию — приглушённо.</summary>
    public int MusicVolume { get; set; } = 25;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HNY", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Повреждённый или недоступный файл — начинаем с настроек по умолчанию.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не получилось сохранить — настройки просто не переживут перезапуск.
        }
    }
}
