using System.Security.Cryptography;
using SpeechAnalyzer.Audio.Windows;
using SpeechAnalyzer.Core;
using SpeechAnalyzer.Testing;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0.26100")]

if (args.Length > 0 && Path.GetFileName(args[0]) == "--protocol-fixture-model")
{
    if (args.Length != 2 || args[1] != SttInputLimits.WorkerFlag ||
        Environment.GetEnvironmentVariable("SPEECH_ANALYZER_PROTOCOL_FIXTURE") != "1") return 2;
    await SttWorkerSession.RunAsync(Console.OpenStandardInput(), Console.Out, false,
        (samples, _, _) => Task.FromResult(new SttWorkerResult(AudioCases.Hash(samples), false, false, false)),
        null, candidateInput: true);
    return 0;
}

if (args.Length == 5 && args[0] == "pdb")
{
    TestRunOutput.Start([args[0], args[3], args[4]], "pdb");
    PackageDebugIdentity.Verify(args[1], args[2], TestRunOutput.Current.Root);
    return 0;
}

TestRunOutput.Start(args, "rolling");
foreach (var assembly in new[] { typeof(ConversationEngine).Assembly, typeof(SttWorkerSession).Assembly })
{
    var path = assembly.Location;
    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    string expected = Environment.GetEnvironmentVariable(assembly.GetName().Name == "SpeechAnalyzer.Core"
        ? "ROLLING_EXPECTED_CORE" : "ROLLING_EXPECTED_AUDIO") ?? throw new InvalidOperationException("Missing binary gate.");
    Require(hash == expected && Path.GetDirectoryName(path) == AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
        "Candidate assembly mismatch.");
    Console.WriteLine($"IDENTITY {Path.GetFileName(path)} {hash} {assembly.ManifestModule.ModuleVersionId}");
}

List<(string Name, Func<Task> Run)> cases =
[
    ("Configuration", RollingCases.Configuration),
    ("PCM exact delivery and upper guard", AudioCases.Protocol),
    ("Production PCM sender to fake worker", AudioCases.Sender),
    ("Invalid timing retains raw fields", AudioCases.Timing),
    ("30-second blocks and required tail", AudioCases.Tail),
    ("Unavailable lookback and Stop", AudioCases.NoLookback),
    ("Gap clears lookback", AudioCases.Gap),
    ("PCM queue is bounded", AudioCases.BoundedQueue),
    ("Durable tail ownership and silent coverage", AudioCases.DurableTail)
];
foreach (int seconds in new[] { 30, 47, 60, 90, 120 })
{
    cases.Add(($"n={seconds} eligibility and absolute coverage", () => RollingCases.Timeline(seconds)));
    cases.Add(($"n={seconds} waits for eligible final text", () => RollingCases.WaitForEligibleText(seconds)));
    cases.Add(($"n={seconds} early Answer and Stop", () => RollingCases.EarlyAnswers(seconds)));
    cases.Add(($"n={seconds} concurrent boundary and coalescing", () => RollingCases.Race(seconds)));
    cases.Add(($"n={seconds} restart preserves origin and union", () => RollingCases.Restart(seconds)));
}
cases.Add(("Save retry before commit", () => RollingCases.SaveRetry(false)));
cases.Add(("Lost acknowledgment after commit", () => RollingCases.SaveRetry(true)));
cases.Add(("Uncertain background and explicit reconciliation", RollingCases.Uncertainty));
cases.Add(("Rejected background and accepted successor", RollingCases.Rejection));
cases.Add(("Long controlled session", RollingCases.LongSession));
int failures = 0;
foreach (var test in cases)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{cases.Count - failures}/{cases.Count} passed");
return failures == 0 ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

internal static class Check
{
    public static void Is(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static Task Idle(ConversationEngine engine) => engine.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

    public static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ControlledClock : IClock
{
    public long Milliseconds { get; set; }
    public TimeSpan Now => TimeSpan.FromMilliseconds(Milliseconds);
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Now;
}
