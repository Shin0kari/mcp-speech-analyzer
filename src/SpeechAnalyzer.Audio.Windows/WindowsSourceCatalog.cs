using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.Audio.Windows;

// Создаётся и освобождается на UI-потоке с message loop для доставки WinEvent.
public sealed class WindowsSourceCatalog : IAudioSourceCatalog, IDisposable
{
    private readonly ConcurrentDictionary<nint, long> generations = new();
    private long sequence;
    private readonly WinEvent callback;
    private readonly nint hook;
    public WindowsSourceCatalog()
    {
        callback = (_, _, window, objectId, childId, _, _) =>
        {
            if (window != 0 && objectId == 0 && childId == 0 && generations.ContainsKey(window))
                generations[window] = Interlocked.Increment(ref sequence);
        };
        // EVENT_OBJECT_CREATE..DESTROY, WINEVENT_OUTOFCONTEXT.
        hook = SetWinEventHook(0x8000, 0x8001, 0, callback, 0, 0, 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось отслеживать жизненный цикл окон.");
    }
    public IReadOnlyList<AudioMicrophone> Microphones() => AudioCaptureSession.Microphones().Select(m => new AudioMicrophone(m.Id, m.Name)).ToArray();
    public IReadOnlyList<ApplicationWindow> Windows()
    {
        var result = new List<ApplicationWindow>();
        if (!EnumWindows((window, _) =>
        {
            if (Read(window) is { Eligible: true } item) result.Add(item);
            return true;
        }, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var live = result.Select(w => (nint)w.Identity.Handle).ToHashSet();
        foreach (var old in generations.Keys) if (!live.Contains(old)) generations.TryRemove(old, out _);
        return result.OrderBy(w => w.Application).ThenBy(w => w.Title).ThenBy(w => w.Identity.Handle).ToArray();
    }
    private ApplicationWindow? Read(nint window)
    {
        try
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != 0 || (GetWindowLongPtrW(window, -20).ToInt64() & 0x80) != 0) return null;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) != 0 || cloaked != 0) return null;
            var title = new StringBuilder(32768);
            if (GetWindowTextW(window, title, title.Capacity) == 0) return null;
            var className = new StringBuilder(256);
            if (GetClassNameW(window, className, className.Capacity) == 0) return null;
            if (GetWindowThreadProcessId(window, out uint pid) == 0 || pid == Environment.ProcessId) return null;
            using var process = Process.GetProcessById((int)pid);
            string? executable = process.MainModule?.FileName;
            if (executable is null || process.HasExited) return null;
            long started = process.StartTime.ToUniversalTime().Ticks;
            long generation = generations.GetOrAdd(window, _ => Interlocked.Increment(ref sequence));
            if (GetWindowThreadProcessId(window, out uint after) == 0 || pid != after) return null;
            string application = process.MainModule?.FileVersionInfo.ProductName ?? process.ProcessName;
            return new(new(window.ToInt64(), (int)pid, started, generation), executable, application,
                className.ToString(), title.ToString(), Minimized: IsIconic(window));
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
        { return null; } // Недоступные/завершившиеся процессы не становятся кандидатами.
    }
    public ApplicationWindow? Find(WindowIdentity identity)
    {
        var current = Read((nint)identity.Handle);
        return current?.Identity == identity ? current : null;
    }
    public void Dispose() { UnhookWinEvent(hook); GC.KeepAlive(callback); }
    private delegate bool EnumWindow(nint window, nint parameter);
    private delegate void WinEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint first, uint last, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
