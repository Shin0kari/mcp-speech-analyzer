using System.Text.Json;

namespace SpeechAnalyzer.Core;

// Служебная настройка; отсутствие файла сохраняет прежний поиск base по умолчанию.
public sealed record SttModelPreferences(int Version = 1, string? ModelPath = null)
{
    public string ResolveModelPath(Func<string> findDefault) => ModelPath ?? findDefault();

    public static SttModelPreferences Select(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("Укажите путь к локальной модели Whisper.");
        string fullPath = Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath)) throw new IOException("Выбранная модель Whisper не найдена; прежний выбор сохранён.");
        return new(ModelPath: fullPath);
    }

    public static SttModelPreferences Load(string path)
    {
        if (!File.Exists(path)) return new();
        var value = JsonSerializer.Deserialize<SttModelPreferences>(File.ReadAllText(path))
            ?? throw new IOException("Пустые настройки STT-модели.");
        value.Validate();
        // Удалённая модель остаётся выбранной: подмена на base запрещена.
        return value;
    }

    public void Save(string path)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicTextFile.Write(path, JsonSerializer.Serialize(this));
    }

    private void Validate()
    {
        if (Version != 1 || ModelPath is not null && (string.IsNullOrWhiteSpace(ModelPath) || !Path.IsPathFullyQualified(ModelPath)))
            throw new IOException("Неверный формат настроек STT-модели.");
    }
}
