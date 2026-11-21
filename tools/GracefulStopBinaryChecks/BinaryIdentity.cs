using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SpeechAnalyzer.Core;

internal static class BinaryIdentity
{
    // Runs before either existing test entry point. Missing expectations fail closed.
    [ModuleInitializer]
    internal static void Verify()
    {
        string expected = Environment.GetEnvironmentVariable("STOP_EXPECTED_CORE")
            ?? throw new InvalidOperationException("STOP_EXPECTED_CORE is required");
        string directory = Environment.GetEnvironmentVariable("STOP_BINARY_DIRECTORY")
            ?? throw new InvalidOperationException("STOP_BINARY_DIRECTORY is required");
        var assembly = typeof(BufferedTranscriptSource).Assembly;
        string location = Path.GetFullPath(assembly.Location);
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "SpeechAnalyzer.Core.dll"));
        string candidate = Path.GetFullPath(Path.Combine(directory, "SpeechAnalyzer.Core.dll"));
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        if (!string.Equals(location, output, StringComparison.OrdinalIgnoreCase)
            || Hash(output) != expected || Hash(candidate) != expected || Hash(location) != expected)
            throw new InvalidOperationException("Production assembly identity mismatch; scenarios prohibited");
        Console.WriteLine("IDENTITY " + JsonSerializer.Serialize(new {
            location, candidate, sha256 = Hash(location), assembly = assembly.FullName,
            mvid = assembly.ManifestModule.ModuleVersionId, gate = "PASS"
        }));
    }
}
