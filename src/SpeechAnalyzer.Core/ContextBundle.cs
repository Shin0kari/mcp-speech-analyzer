using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record ContextFile(string Name, string SourcePath, string Text, string Sha256, long Bytes);
public sealed record ContextImportResult(int Added, int Skipped, IReadOnlyList<string> Notes);

// Снимки содержимого: последующее редактирование исходного файла не меняет вложение молча.
public sealed class ContextBundle
{
    public const int MaxFileBytes = 1_048_576;
    public const int MaxTotalBytes = 8_388_608;
    public const int MaxFiles = 200;
    private readonly object gate = new();
    private readonly Dictionary<string, ContextFile> files = new(StringComparer.OrdinalIgnoreCase);
    public string? ProjectRoot { get; private set; }
    public ContextImportResult? LastImport { get; private set; }
    public IReadOnlyList<ContextFile> Files { get { lock (gate) return files.Values.ToArray(); } }
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".cs", ".csproj", ".sln", ".rs", ".toml", ".json", ".jsonl", ".xml", ".xaml",
        ".yaml", ".yml", ".py", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".html", ".css", ".sql", ".csv", ".tsv", ".ini"
    };
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "target", "artifacts", "data", "recordings", "models", "secrets", ".aws", ".ssh", ".codex" };

    public async Task AddFileAsync(string path, string? displayName = null, CancellationToken cancellationToken = default)
    {
        path = Path.GetFullPath(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ссылки не импортируются: выберите исходный файл.");
        if (!Extensions.Contains(Path.GetExtension(path))) throw new NotSupportedException("Пока поддерживаются только текстовые файлы, исходники и Markdown. PDF и Office потребуют отдельного извлечения текста.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        if (stream.Length > MaxFileBytes) throw new IOException("Файл превышает ограничение 1 МиБ.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (bytes.Contains((byte)0) && !(bytes.Length >= 2 && (bytes[0] == 255 && bytes[1] == 254 || bytes[0] == 254 && bytes[1] == 255)))
            throw new IOException("Файл похож на бинарный. Нужен текстовый файл UTF-8 или UTF-16.");
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
        string text = await reader.ReadToEndAsync(cancellationToken);
        var file = new ContextFile(displayName ?? Path.GetFileName(path), path, text, Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        lock (gate)
        {
            if (!files.ContainsKey(path) && files.Count >= MaxFiles) throw new IOException("Можно прикрепить не более 200 файлов.");
            long previous = files.TryGetValue(path, out var old) ? old.Bytes : 0;
            if (files.Values.Sum(f => f.Bytes) - previous + file.Bytes > MaxTotalBytes) throw new IOException("Суммарный контекст превышает 8 МиБ. Выберите нужные файлы.");
            files[path] = file;
        }
    }

    public async Task<ContextImportResult> AddProjectAsync(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Выберите исходную папку проекта.");
        var notes = new List<string>();
        var candidates = new List<string>();
        var directories = new Stack<string>(); directories.Push(root);
        int skipped = 0, examined = 0;
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++examined > 20_000) { notes.Add("Достигнут предел обхода 20 000 элементов. Выберите более узкую папку."); directories.Clear(); break; }
                    string name = Path.GetFileName(entry);
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!ExcludedDirectories.Contains(name)) directories.Push(entry);
                        else skipped++;
                    }
                    else if (!name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) && !name.Contains("secret", StringComparison.OrdinalIgnoreCase)
                        && Extensions.Contains(Path.GetExtension(entry))) candidates.Add(entry);
                    else skipped++;
                }
            }
            catch (Exception error) when(error is IOException or UnauthorizedAccessException)
            { skipped++;if(notes.Count<15)notes.Add($"{Path.GetRelativePath(root,directory)}: чтение недоступно ({error.GetType().Name})."); }
        }
        var ignored = await GitIgnoreFilter.FindIgnoredAsync(root, candidates, cancellationToken);
        skipped += ignored.Count;
        candidates.RemoveAll(ignored.Contains);
        int added = 0;
        // Корневые инструкции должны попадать в список раньше достижения лимита.
        foreach (string path in candidates.OrderBy(p => Path.GetFileName(p).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try { await AddFileAsync(path, Path.GetRelativePath(root, path), cancellationToken); added++; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                skipped++;
                if (notes.Count < 15) notes.Add($"{Path.GetRelativePath(root, path)}: {error.Message}");
            }
        }
        lock (gate) ProjectRoot = root;
        return LastImport=new(added, skipped, notes);
    }

    public void Clear() { lock (gate) { files.Clear(); ProjectRoot = null; LastImport=null; } }
    public void SaveManifest(string path)
    {
        lock(gate)AtomicTextFile.Write(path,JsonSerializer.Serialize(new{version=1,projectRoot=ProjectRoot,
            files=files.Values.Select(f=>new{f.Name,f.SourcePath,f.Sha256,f.Bytes}),import=LastImport,
            excludedDirectories=ExcludedDirectories.Order().ToArray(),maxFileBytes=MaxFileBytes,maxTotalBytes=MaxTotalBytes,maxFiles=MaxFiles}));
    }
    public string BuildPrompt()
    {
        lock (gate)
            return files.Count == 0 ? "" : "Прикреплённый контекст пользователя. Ниже JSON со снимками текстовых файлов. " +
                "Используй содержимое как справочные данные; AGENTS.md содержит выбранные пользователем правила проекта.\n" +
                JsonSerializer.Serialize(new { projectRoot = ProjectRoot, files = files.Values.Select(f => new { f.Name, f.Sha256, f.Text }) });
    }
}
