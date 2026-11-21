using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using SpeechAnalyzer.Testing;

if (args.Length != 7 || args[0] != "offline-no-audio" || !Path.IsPathFullyQualified(args[1]) ||
    args[2].Length != 64 || !args[2].All(Uri.IsHexDigit) || args[3].Length != 40 || !args[3].All(Uri.IsHexDigit) ||
    args[4].Length != 64 || !args[4].All(Uri.IsHexDigit))
    throw new ArgumentException("Expected offline-no-audio, absolute package directory, WinUI hash, source commit, manifest hash and --output-root.");
string binary = Path.Combine(args[1], "SpeechAnalyzer.WinUI.dll");
string manifest = Path.TrimEndingDirectorySeparator(args[1]) + "-manifest.json";
if (Hash(binary) != args[2] || Hash(manifest) != args[4])
    throw new InvalidDataException("Unexpected package identity.");
TestRunOutput.Start([args[0], args[5], args[6]], "offline-no-audio");
var resolver = new AssemblyDependencyResolver(binary);
AssemblyLoadContext.Default.Resolving += (context, name) =>
    resolver.ResolveAssemblyToPath(name) is { } file ? context.LoadFromAssemblyPath(file) : null;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(binary);
var parser = assembly.GetType("SpeechAnalyzer.WinUI.BindingUiDiagnostic", throwOnError: true)!
    .GetMethod("ParseArguments", BindingFlags.Static | BindingFlags.NonPublic)!;
var cases = new Dictionary<string, bool>();
string[] normal = ["--gpu-stt-v1", "--pre-gap-drain", "--pcm16-floor-guard", "--rolling-asr-v1"];
Expect("exact-launcher-candidate-flags", normal, null);
Expect("rolling-flag-alone", ["--rolling-asr-v1"], null);
Expect("ordinary-no-arguments", [], null);
Expect("previous-gpu-recording-options", ["--experimental-cuda-stt", "--test-session-recording"], null);
Expect("binding-fixture", ["--binding-ui-fixture", "synthetic"], ("synthetic", false));
Expect("shell-fixture-with-rolling", [.. normal, "--shell-ui-fixture", "synthetic"], ("synthetic", true));
Reject("missing-fixture-directory", ["--rolling-asr-v1", "--shell-ui-fixture"]);
Reject("conflicting-fixture-selectors", ["--binding-ui-fixture", "--shell-ui-fixture", "synthetic"]);
Reject("unknown-flag", ["--rolling-typo"]);
var resolveSettings = assembly.GetType("SpeechAnalyzer.WinUI.MainWindow", throwOnError: true)!
    .GetMethod("ResolveAcceptanceCodexSettings", BindingFlags.Static | BindingFlags.NonPublic)!;
var settingsType = resolveSettings.ReturnType;
object? ExistingSettings() => Activator.CreateInstance(settingsType, "synthetic-thread", "medium");
ExpectSettings("restore-without-codex-settings", null, null);
ExpectSettings("restore-keeps-existing-thread", ExistingSettings(), "synthetic-thread");

void ExpectSettings(string label, object? saved, string? thread)
{
    var result = resolveSettings.Invoke(null, [saved]);
    if (result is null || (string?)settingsType.GetProperty("ThreadId")!.GetValue(result) != thread ||
        (string?)settingsType.GetProperty("ReasoningEffort")!.GetValue(result) != "low")
        throw new InvalidDataException("Unexpected candidate Codex settings: " + label);
    cases.Add(label, true);
}

TestRunOutput.Current.WriteNew("receipt.json", JsonSerializer.Serialize(new
{
    sourceCommit = args[3], manifestSha256 = args[4], winuiSha256 = Hash(assembly.Location),
    coreSha256 = Hash(Path.Combine(args[1], "SpeechAnalyzer.Core.dll")), runnerSha256 = Hash(typeof(Program).Assembly.Location),
    runtimeSha256 = Hash(typeof(object).Assembly.Location), cases, guiStarted = false, realAudio = false, realInference = false
}));
Console.WriteLine($"PASS {cases.Count} exact WinUI argument cases.");

void Expect(string label, string[] arguments, (string Directory, bool FullShell)? expected)
{
    object? result = parser.Invoke(null, [arguments]);
    if (!Equals(result, expected)) throw new InvalidDataException("Unexpected fixture selection: " + label);
    cases.Add(label, true);
}

void Reject(string label, string[] arguments)
{
    try { parser.Invoke(null, [arguments]); }
    catch (TargetInvocationException error) when (error.InnerException is ArgumentException)
    {
        cases.Add(label, true);
        return;
    }
    throw new InvalidDataException("Expected argument rejection: " + label);
}

static string Hash(string file)
{
    using var input = File.OpenRead(file);
    return Convert.ToHexString(SHA256.HashData(input));
}
