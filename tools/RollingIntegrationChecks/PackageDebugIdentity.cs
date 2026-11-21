using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PackageDebugIdentity
{
    public static void Verify(string package, string sourceRoot, string evidence)
    {
        var entries = new[] { "SpeechAnalyzer.Core", "SpeechAnalyzer.Audio.Windows", "SpeechAnalyzer.WinUI",
            "NAudio.Wasapi", "stt-worker/SttWorker" };
        var receipts = new List<object>();
        foreach (string entry in entries)
        {
            string path = Path.Combine(package, entry);
            using var image = new PEReader(File.OpenRead(path + ".dll"));
            var debug = image.ReadDebugDirectory().Single(item => item.Type == DebugDirectoryEntryType.CodeView);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(path + ".pdb"));
            var metadata = provider.GetMetadataReader();
            byte[] id = metadata.DebugMetadataHeader!.Id.ToArray();
            Check.Is(new Guid(id.AsSpan(0, 16)) == image.ReadCodeViewDebugDirectoryData(debug).Guid
                && BinaryPrimitives.ReadUInt32LittleEndian(id.AsSpan(16)) == debug.Stamp, "PE/PDB identifier mismatch: " + entry);
            var documents = new List<object>();
            var virtualGeneratedDocuments = new List<object>();
            foreach (var handle in metadata.Documents)
            {
                var document = metadata.GetDocument(handle);
                string source = metadata.GetString(document.Name);
                if (document.Hash.IsNil) continue;
                string verifiedPath = source;
                if (!File.Exists(source))
                {
                    string buildObjectRoot = Path.Combine(Path.GetDirectoryName(evidence)!, "build", "obj");
                    string relative = Path.GetRelativePath(buildObjectRoot, source);
                    var parts = relative.Split(Path.DirectorySeparatorChar);
                    Check.Is(parts.Length >= 5 && parts[0] is "SpeechAnalyzer.WinUI" or "NAudio.Timing",
                        "Compiled source unavailable: " + source);
                    verifiedPath = Path.Combine(Path.GetDirectoryName(evidence)!, "generated-build", "obj", parts[0], parts[1],
                        "generated", Path.Combine(parts[2..]));
                    Check.Is(File.Exists(verifiedPath), "Generator output unavailable: " + verifiedPath);
                    virtualGeneratedDocuments.Add(new { path = Path.GetRelativePath(sourceRoot, source),
                        verifiedPath = Path.GetRelativePath(sourceRoot, verifiedPath) });
                }
                Check.Is(Path.IsPathFullyQualified(source), "Compiled source path is not absolute: " + source);
                string hash = Hash(verifiedPath);
                Check.Is(hash == Convert.ToHexString(metadata.GetBlobBytes(document.Hash)), "Source/PDB hash mismatch: " + source);
                documents.Add(new { path = Path.GetRelativePath(sourceRoot, source), sha256 = hash });
            }
            Check.Is(documents.Count > 0, "Missing PDB source documents: " + entry);
            receipts.Add(new { entry, dllSha256 = Hash(path + ".dll"), pdbSha256 = Hash(path + ".pdb"), documents, virtualGeneratedDocuments });
        }

        string output = Path.Combine(evidence, "pe-pdb-source.json");
        using var receipt = new FileStream(output, FileMode.CreateNew);
        JsonSerializer.Serialize(receipt, receipts, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine($"Verified {receipts.Count} PE/PDB groups and compiled source hashes.");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
