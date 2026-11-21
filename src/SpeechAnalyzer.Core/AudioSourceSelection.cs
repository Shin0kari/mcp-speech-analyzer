using System.Text.Json;

namespace SpeechAnalyzer.Core;

public enum WindowMatchPolicy { SpecificWindow, Application }
public enum AudioSelectionChange { Both, Microphone, Window, WindowPolicy }
public sealed record WindowIdentity(long Handle, int ProcessId, long ProcessStartedUtcTicks, long Generation);
public sealed record ApplicationWindow(WindowIdentity Identity, string Executable, string Application, string ClassName,
    string Title, bool Visible = true, bool ToolWindow = false, bool Owned = false, bool Cloaked = false, bool Minimized = false)
{
    public bool Eligible => Visible && !ToolWindow && !Owned && !Cloaked && !string.IsNullOrWhiteSpace(Title)
        && !string.IsNullOrWhiteSpace(Executable) && !string.IsNullOrWhiteSpace(ClassName);
}
public sealed record AudioMicrophone(string DeviceId, string Label);
public interface IAudioSourceCatalog
{
    IReadOnlyList<ApplicationWindow> Windows();
    IReadOnlyList<AudioMicrophone> Microphones();
    ApplicationWindow? Find(WindowIdentity identity) => Windows().FirstOrDefault(w => w.Identity == identity);
}
public sealed record WindowSelectionRule(string Executable, string ClassName, string Title, WindowMatchPolicy Policy);
public sealed record AudioSelectionSettings(int Version, string? MicrophoneDeviceId, WindowSelectionRule? Window);
public sealed record AudioSourceEntry(string SourceId, string Kind, string Label, bool Minimized = false);
public sealed record AudioSelectionStatus(AudioSelectionSettings Selected, string Availability, string MicrophoneAvailability,
    string? WindowSourceId, string? MicrophoneSourceId, string? Label, long CatalogVersion);
public sealed record AudioCaptureTarget(string MicrophoneDeviceId, ApplicationWindow Window);

// Не зависит от UI/Win32. Все изменения выбора проходят через очередь SessionCoordinator.
public sealed class AudioSourceSelection
{
    private readonly object gate = new();
    private readonly IAudioSourceCatalog catalog;
    private Action<AudioSelectionSettings> save;
    private Dictionary<string, ApplicationWindow> windows = [];
    private Dictionary<string, AudioMicrophone> microphones = [];
    private ApplicationWindow? bound;
    private AudioSelectionSettings settings;
    private long catalogVersion;
    public AudioSourceSelection(IAudioSourceCatalog catalog, AudioSelectionSettings? settings = null, Action<AudioSelectionSettings>? save = null)
    {
        this.catalog = catalog; this.save = save ?? (_ => { });
        this.settings = settings ?? new(1, null, null);
        if (this.settings.Version != 1) throw new IOException("Неизвестная версия выбора источников.");
    }
    public IReadOnlyList<AudioSourceEntry> Refresh()
    {
        lock (gate)
        {
            // Каждый снимок имеет новые ID. Старый ID никогда не переназначается.
            var nextWindows = catalog.Windows().Where(w => w.Eligible).ToDictionary(_ => Guid.NewGuid().ToString("N"));
            var nextMicrophones = catalog.Microphones().ToDictionary(_ => Guid.NewGuid().ToString("N"));
            windows = nextWindows; microphones = nextMicrophones;
            catalogVersion++;
            Rebind(windows.Values.ToArray());
            return microphones.Select(p => new AudioSourceEntry(p.Key, "microphone", p.Value.Label))
                .Concat(windows.Select(p => new AudioSourceEntry(p.Key, "application_window",
                    $"{p.Value.Application} — {p.Value.Title} · {p.Key[..6]}", p.Value.Minimized))).ToArray();
        }
    }
    private static bool SameWindow(ApplicationWindow a, ApplicationWindow b) => a.Identity == b.Identity
        && string.Equals(a.Executable, b.Executable, StringComparison.OrdinalIgnoreCase) && a.ClassName == b.ClassName;
    private ApplicationWindow[] Matches(IEnumerable<ApplicationWindow> current) => settings.Window is not { } rule ? [] :
        current.Where(w => w.Eligible && string.Equals(w.Executable, rule.Executable, StringComparison.OrdinalIgnoreCase)
            && w.ClassName == rule.ClassName && (rule.Policy == WindowMatchPolicy.Application || w.Title == rule.Title)).ToArray();
    private void Rebind(IReadOnlyList<ApplicationWindow> current)
    {
        if (bound is not null && current.FirstOrDefault(w => SameWindow(w, bound)) is { } live) { bound = live; return; }
        var matches = Matches(current);
        bound = matches.Length == 1 ? matches[0] : null;
    }
    public void ValidateRequest(string? microphoneSourceId, string? windowSourceId, long? expectedCatalogVersion, AudioSelectionChange change = AudioSelectionChange.Both)
    {
        lock (gate)
        {
            if (expectedCatalogVersion is { } expected && expected != catalogVersion)
                throw new StaleAudioCatalogException(catalogVersion);
            if (!Enum.IsDefined(change)) throw new ArgumentException("Неизвестная часть выбора источников.");
            if (change is AudioSelectionChange.Both or AudioSelectionChange.Microphone && microphoneSourceId is not null && !microphones.ContainsKey(microphoneSourceId) ||
                change is AudioSelectionChange.Both or AudioSelectionChange.Window && windowSourceId is not null && !windows.ContainsKey(windowSourceId))
                throw new StaleAudioCatalogException(catalogVersion);
        }
    }
    public void Select(string? microphoneSourceId, string? windowSourceId, WindowMatchPolicy policy, long? expectedCatalogVersion = null, AudioSelectionChange change = AudioSelectionChange.Both)
    {
        lock (gate)
        {
            ValidateRequest(microphoneSourceId, windowSourceId, expectedCatalogVersion, change);
            if (!Enum.IsDefined(policy)) throw new ArgumentException("Неизвестное правило сопоставления окна.");
            bool changeMicrophone = change is AudioSelectionChange.Both or AudioSelectionChange.Microphone;
            bool changeWindow = change is AudioSelectionChange.Both or AudioSelectionChange.Window;
            AudioMicrophone? mic = null;
            ApplicationWindow? window = null;
            if (changeMicrophone && microphoneSourceId is not null && !microphones.TryGetValue(microphoneSourceId, out mic))
                throw new StaleAudioCatalogException(catalogVersion);
            if (changeWindow && windowSourceId is not null && !windows.TryGetValue(windowSourceId, out window))
                throw new StaleAudioCatalogException(catalogVersion);
            if (window is not null && !catalog.Windows().Any(w => w.Eligible && SameWindow(w, window)) ||
                mic is not null && !catalog.Microphones().Any(m => m.DeviceId == mic.DeviceId))
                throw new InvalidOperationException("Источник исчез или изменился. Обновите список.");
            var windowRule = changeWindow ? (window is null ? null : new WindowSelectionRule(window.Executable, window.ClassName, window.Title, policy))
                : change == AudioSelectionChange.WindowPolicy && settings.Window is { } rule ? rule with { Policy = policy } : settings.Window;
            var next = new AudioSelectionSettings(1, changeMicrophone ? mic?.DeviceId : settings.MicrophoneDeviceId, windowRule);
            if (next != settings) save(next); // Сбой сохранения не меняет текущий выбор.
            settings = next;
            if (changeWindow) bound = window;
        }
    }
    public AudioSelectionStatus Status()
    {
        lock (gate)
        {
            var current = catalog.Windows().Where(w => w.Eligible).ToArray();
            var live = bound is null ? null : current.FirstOrDefault(w => SameWindow(w, bound));
            string availability = settings.Window is null ? "not_selected" : live is not null ? "available" :
                Matches(current).Length > 1 ? "ambiguous" : "unavailable";
            string micState = settings.MicrophoneDeviceId is null ? "not_selected" :
                catalog.Microphones().Any(m => m.DeviceId == settings.MicrophoneDeviceId) ? "available" : "unavailable";
            return new(settings, availability, micState,
                live is null ? null : windows.FirstOrDefault(p => SameWindow(p.Value, live)).Key,
                microphones.FirstOrDefault(p => p.Value.DeviceId == settings.MicrophoneDeviceId).Key,
                live is null ? null : $"{live.Application} — {live.Title}", catalogVersion);
        }
    }
    public AudioCaptureTarget RequireTarget()
    {
        lock (gate)
        {
            var status = Status();
            if (status.Availability != "available" || status.MicrophoneAvailability != "available")
                throw new InvalidOperationException($"Источники: окно {status.Availability}, микрофон {status.MicrophoneAvailability}. Обновите список и выберите источник.");
            var live = catalog.Windows().SingleOrDefault(w => w.Eligible && SameWindow(w, bound!))
                ?? throw new IOException("Выбранное окно исчезло перед стартом.");
            return new(settings.MicrophoneDeviceId!, live);
        }
    }
    public bool IsCurrent(ApplicationWindow target) => catalog.Find(target.Identity) is { Eligible: true } live && SameWindow(live, target);
}

public sealed class StaleAudioCatalogException(long currentVersion) : InvalidOperationException("stale_catalog")
{
    public long CurrentVersion { get; } = currentVersion;
}

public sealed record AudioGlobalPreferences(int Version, string? LastSuccessfulMicrophoneDeviceId)
{
    public static AudioGlobalPreferences Load(string path)
    {
        var preferences = File.Exists(path)
            ? JsonSerializer.Deserialize<AudioGlobalPreferences>(File.ReadAllText(path)) ?? throw new IOException("Пустые общие настройки аудио.")
            : new(1, null);
        if (preferences.Version != 1) throw new IOException("Неизвестная версия общих настроек аудио.");
        return preferences;
    }
    public static void Save(string path, AudioGlobalPreferences settings) => AudioSelectionFile.SaveJson(path, settings);
}

public static class AudioSelectionFile
{
    public static AudioSelectionSettings? Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<AudioSelectionSettings>(File.ReadAllText(path)) ?? throw new IOException("Пустые настройки источников.") : null;
    public static AudioSelectionSettings LoadForSession(string path) => Load(path) ?? new(1, null, null);
    public static void Save(string path, AudioSelectionSettings settings)
        => SaveJson(path, settings);
    public static void SaveJson<T>(string path, T settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings))); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
