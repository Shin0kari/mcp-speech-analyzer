using Microsoft.UI.Xaml;

namespace SpeechAnalyzer.WinUI;
public partial class App : Application
{
    private Window? window;
    public App()
    {
        UnhandledException += (_, args) => WriteStartupDiagnostic(args.Exception);
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception error) { WriteStartupDiagnostic(error); throw; }
    }
    private static void WriteStartupDiagnostic(Exception error)
    {
        // Только явная диагностика разработчика, без автоматической записи приватных журналов.
        if (Environment.GetEnvironmentVariable("SPEECH_ANALYZER_STARTUP_DIAGNOSTIC") is not { Length: > 0 } path) return;
        try { File.WriteAllText(path, error.ToString()); } catch { }
    }
}
