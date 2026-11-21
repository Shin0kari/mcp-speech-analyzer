using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SpeechAnalyzer.Testing;

// A small Windows test-output contract, not a production storage abstraction.
public sealed class TestRunOutput
{
    public string Root { get; }
    public static TestRunOutput Current { get; private set; } = null!;
    private TestRunOutput(string root) => Root = root;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

    public static string Start(string[] args, params string[] selectors)
    {
        // Validate selection before touching any output, including the run marker.
        if (args.Length != 3 || !selectors.Contains(args[0], StringComparer.Ordinal)
            || args[1] != "--output-root" || !Path.IsPathFullyQualified(args[2]))
            throw new ArgumentException("Use one explicit allowlisted selector and --output-root <fresh absolute directory>.");
        if (Current is not null) throw new InvalidOperationException("A run is already initialized.");
        Current = CreateFresh(args[2]);
        string temp = Current.NewDirectory("temp");
        Environment.SetEnvironmentVariable("TEMP", temp);
        Environment.SetEnvironmentVariable("TMP", temp);
        return args[0];
    }

    public static TestRunOutput CreateFresh(string absoluteRoot)
    {
        if (!Path.IsPathFullyQualified(absoluteRoot)) throw new ArgumentException("Absolute output root required.");
        string root = Path.GetFullPath(absoluteRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetDirectoryName(root) ?? throw new ArgumentException("A filesystem root is not a test run.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Create the intended parent explicitly before the run.");
        RejectLinks(parent);
        // Win32 mkdir is atomic and fails for an existing directory; no check/create race.
        if (!CreateDirectoryW(root, IntPtr.Zero)) throw new IOException("Fresh run directory required: " + root, new Win32Exception(Marshal.GetLastWin32Error()));
        var output = new TestRunOutput(root);
        output.WriteNew(".test-run", "Speech Analyzer isolated test run v1\n");
        return output;
    }

    private static void RejectLinks(string path)
    {
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not test output destinations: " + part);
    }

    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new ArgumentException("Only relative paths inside the run root are writable.");
        if (relative.Split(['/', '\\']).Any(component => component.EndsWith(' ') || component.EndsWith('.')))
            throw new ArgumentException("Ambiguous Windows path components are not writable test paths.");
        string full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Test output escapes its run root.");
        RejectLinks(full);
        return full;
    }

    public string NewDirectory(string relative)
    {
        string full = Resolve(relative);
        if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException("Create output parents explicitly.");
        if (!CreateDirectoryW(full, IntPtr.Zero)) throw new IOException("Fresh fixture directory required: " + full, new Win32Exception(Marshal.GetLastWin32Error()));
        return full;
    }

    public void WriteNew(string relative, string text)
    {
        using var file = new FileStream(Resolve(relative), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(Encoding.UTF8.GetBytes(text)); file.Flush(true);
    }
}
