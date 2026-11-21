using System.Text.Json;

namespace SpeechAnalyzer.Core;

public enum ThemePreference { System, Dark, Light }
public sealed record AppearancePreferences(ThemePreference Theme = ThemePreference.System)
{
    public static AppearancePreferences Load(string path)
    {
        var value = File.Exists(path) ? JsonSerializer.Deserialize<AppearancePreferences>(File.ReadAllText(path))
            ?? throw new IOException("Пустые настройки оформления.") : new();
        if (!Enum.IsDefined(value.Theme)) throw new IOException("Неизвестная тема оформления.");
        return value;
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicTextFile.Write(path, JsonSerializer.Serialize(this));
    }
}
