using System.Globalization;
using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record RollingContextConfiguration
{
    public const string EnvironmentKey = "SPEECH_ANALYZER_ROLLING_SECONDS";
    public const int ScheduleVersion = 2;
    public const int FirstEligibilityUnits = 2;
    public const int DefaultSeconds = 60;
    public const int MinSeconds = 30;
    public const int MaxSeconds = 120;
    public int Seconds { get; }
    public long UnitMs => Seconds * 1000L;

    public RollingContextConfiguration(int seconds)
    {
        if (seconds is < MinSeconds or > MaxSeconds) throw new ArgumentOutOfRangeException(nameof(seconds));
        Seconds = seconds;
    }

    public static RollingContextConfiguration Parse(string? value)
    {
        if (value is null) return new(DefaultSeconds);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            throw new ArgumentException($"{EnvironmentKey} must be an integer in [{MinSeconds}, {MaxSeconds}].");
        return new(seconds);
    }
}

public sealed partial class SessionJournal
{
    private sealed record RollingMetadata(int Version, int Seconds);

    public RollingContextConfiguration BindRollingContext(Func<RollingContextConfiguration> configuration)
    {
        lock (gate)
        {
            string path = Path.Combine(DirectoryPath, "rolling-context.json");
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 1024) throw new IOException("Invalid rolling context metadata size.");
                var saved = JsonSerializer.Deserialize<RollingMetadata>(File.ReadAllText(path));
                if (saved is { Version: 1 })
                    throw new IOException("This conversation uses the 3n schedule. Open it with its original candidate.");
                if (saved is not { Version: RollingContextConfiguration.ScheduleVersion })
                    throw new IOException("Unknown rolling context schedule version.");
                return new(saved.Seconds);
            }
            if (operations.Values.Any(operation => operation.Request.RollingContextSeconds.HasValue))
                throw new IOException("Rolling schedule metadata is missing for this conversation.");
            var selected = configuration();
            string content = JsonSerializer.Serialize(new RollingMetadata(RollingContextConfiguration.ScheduleVersion, selected.Seconds));
            try { WriteAtomic(path, content); }
            catch when (File.Exists(path) && File.ReadAllText(path) == content) { }
            return selected;
        }
    }
}
