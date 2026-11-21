using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpeechAnalyzer.Core;

public sealed record ConversationStoragePreferences(int Version = 1, string? RootDirectory = null, IReadOnlyList<string>? PreviousRoots = null)
{
    public ConversationStoragePreferences WithRoot(string? directory) => new(1, directory,
        new[] { ResolveDirectory() }.Concat(PreviousRoots ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    [JsonIgnore]
    public string[] KnownDirectories => new[] { ResolveDirectory() }.Concat(PreviousRoots ?? []).Select(path =>
        Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : throw new IOException("Сохранён относительный путь к разговорам."))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public string ResolveDirectory(string? documentsDirectory = null)
    {
        if (Version != 1) throw new IOException("Неизвестная версия настроек хранения разговоров.");
        if (RootDirectory is { } selected)
        {
            if (!Path.IsPathFullyQualified(selected)) throw new IOException("Каталог разговоров должен иметь полный путь.");
            return Path.GetFullPath(selected);
        }
        string documents = documentsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents)) throw new IOException("Windows не вернула каталог Documents. Выберите папку разговоров в настройках.");
        return Path.Combine(documents, "Speech Analyzer", "Conversations");
    }
    public static ConversationStoragePreferences Load(string path)
    {
        var value = File.Exists(path) ? JsonSerializer.Deserialize<ConversationStoragePreferences>(File.ReadAllText(path))
            ?? throw new IOException("Пустые настройки хранения разговоров.") : new();
        _ = value.KnownDirectories; return value;
    }
    public void Save(string path)
    {
        _ = KnownDirectories;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicTextFile.Write(path, JsonSerializer.Serialize(this));
    }
}
