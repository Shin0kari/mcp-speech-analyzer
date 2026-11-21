using System.Diagnostics;
using System.Text;

namespace SpeechAnalyzer.Core;

// Синтаксис gitignore делегирован Git: вложенные правила, отрицания, ** и экранирование.
internal static class GitIgnoreFilter
{
    public static async Task<HashSet<string>> FindIgnoredAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0) return ignored;
        // Изолированные служебные данные позволяют обрабатывать также папки без .git.
        // Исходный проект не инициализируется и не изменяется.
        string metadata = Path.Combine(Path.GetTempPath(), "speech-analyzer-ignore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(metadata, "objects"));
        Directory.CreateDirectory(Path.Combine(metadata, "refs"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(metadata, "HEAD"), "ref: refs/heads/context\n", cancellationToken);
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
            foreach (string argument in new[] { "--no-optional-locks", "--git-dir=" + metadata, "--work-tree=" + root,
                "-c", "core.bare=false", "-c", "core.fsmonitor=false", "check-ignore", "--no-index", "--stdin", "-z" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить Git для проверки .gitignore.");
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
                Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
                foreach (string path in paths) await process.StandardInput.WriteAsync((path + '\0').AsMemory(), cancellationToken);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);
                string result = await output; await errors; // Не выводим пути проекта из stderr.
                if (process.ExitCode is not (0 or 1)) throw new IOException("Git не смог проверить .gitignore. Импорт проекта остановлен; можно прикрепить нужные файлы вручную.");
                foreach (string path in result.Split('\0', StringSplitOptions.RemoveEmptyEntries)) ignored.Add(Path.GetFullPath(path, root));
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        catch (System.ComponentModel.Win32Exception error)
        { throw new IOException("Для импорта проекта с правилами .gitignore нужен Git в PATH. Можно прикрепить файлы вручную.", error); }
        finally { Directory.Delete(metadata, true); }
        return ignored;
    }
}
