using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.WinUI;

// Только явный аргумент и маркер собственного синтетического fixture.
internal sealed class BindingUiDiagnostic
{
    public string DirectoryPath { get; }
    public CodexThreadBinding Binding { get; }
    public bool FullShell { get; private set; }
    private TaskCompletionSource? release;
    public BindingUiDiagnostic(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "binding-ui-fixture.json")));
        if (marker.RootElement.GetProperty("kind").GetString() != "synthetic-binding-ui-v1"
            || new SessionJournal(DirectoryPath).ModelOperations.Count != 0)
            throw new IOException("Нужен синтетический fixture без модельных операций.");
        Binding = new(DirectoryPath);
        if (Binding.State is null) throw new IOException("Нет синтетической привязки.");
    }
    public static BindingUiDiagnostic? FromArguments()
    {
        var fixture = ParseArguments(Environment.GetCommandLineArgs().Skip(1));
        return fixture is { } selected ? new(selected.Directory) { FullShell = selected.FullShell } : null;
    }

    internal static (string Directory, bool FullShell)? ParseArguments(IEnumerable<string> arguments)
    {
        // Capture options are independent of diagnostic fixture selection.
        string[] args = arguments.Where(argument => argument is not ("--experimental-cuda-stt" or "--gpu-stt-v1" or
            "--test-session-recording" or "--pre-gap-drain" or "--pcm16-floor-guard" or SttInputLimits.CandidateFlag)).ToArray();
        if (args.Length == 0) return null;
        if (args.Length != 2 || args[0] is not ("--binding-ui-fixture" or "--shell-ui-fixture"))
            throw new ArgumentException("Диагностика: --binding-ui-fixture или --shell-ui-fixture <каталог>");
        return (args[1], args[0] == "--shell-ui-fixture");
    }
    public void ReleaseRead() => release?.TrySetResult();
    public async Task<ChatBindingEvidence> InspectAsync(string id)
    {
        CodexBindingProbe.ValidateId(id);
        if (id == "slow")
        {
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await release.Task;
        }
        var candidate = await CodexBindingProbe.ReadAsync(id, (method, _) =>
        {
            File.AppendAllText(Path.Combine(DirectoryPath, "diagnostic-reads.jsonl"), JsonSerializer.Serialize(new { method, id }) + "\n");
            if (id == "read-error") throw new IOException("Диагностическая ошибка чтения истории. Прежнее решение сохранено.");
            if (id is not ("synthetic-known" or "slow" or "mismatch")) throw new IOException("Диагностика: ID чата не найден.");
            return Task.FromResult(method switch
            {
                "thread/read" => JsonSerializer.SerializeToElement(new { thread = new { id,
                    cwd = id == "mismatch" ? Path.Combine(DirectoryPath, "foreign") : Binding.State!.WorkingDirectory,
                    name = CodexModelSettings.ThreadName(id), model = CodexModelSettings.Model } }),
                "thread/turns/list" => JsonSerializer.SerializeToElement(new { data = new[] { new { id = "synthetic-turn" } }, nextCursor = (string?)null }),
                _ => throw new IOException("Диагностический адаптер разрешает только чтение.")
            });
        });
        return Binding.ValidateCandidate(id, candidate);
    }
}

public sealed partial class MainWindow
{
    private void InitializeBindingDiagnostic()
    {
        var diagnostic = bindingDiagnostic!;
        journal = new(diagnostic.DirectoryPath);
        codex = new(binding: diagnostic.Binding);
        if (diagnostic.FullShell)
        {
            Title = "Speech Analyzer · обычная оболочка · ИМИТАТОР";
            ModeNotice.Title = "ВИЗУАЛЬНАЯ ПРИЁМКА · ИМИТАТОР";
            ModeNotice.Message = "Синтетическая сессия. Codex, модель, аудио, STT, внешнее управление и пользовательские разговоры недоступны. Сообщения добавляются кнопкой имитатора.";
            ContextStatusText.Text = "ИМИТАТОР: модельные действия выключены. Проверка привязки использует только локальные синтетические сведения.";
            ConversationRootText.Text = "Изолированные настройки: " + PreferencesDirectory;
            for (int i = 1; i <= 24; i++) AddShellMessage(i);
            ShellMessageButton.Visibility = Visibility.Visible;
            UpdateCodexThread();
            return;
        }
        // Та же панель и обработчики, остальные UI-действия недоступны.
        MainLayout.Visibility = Visibility.Collapsed;
        BindingContextPanel.Children.Remove(BindingPanel);
        InputFooter.Children.Remove(Notice);
        var panel = new StackPanel { Spacing = 16, Padding = new Thickness(28), MaxWidth = 1120,
            HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "ДИАГНОСТИКА UI ПРИВЯЗКИ · ИМИТАТОР", FontSize = 24 });
        panel.Children.Add(new TextBlock { Text = "Синтетический разговор. Codex, создание чата, inference, аудио, STT и внешнее управление выключены.\n" +
            "Кандидаты: synthetic-known, mismatch, read-error, slow. Остальные ID не найдены.\nКаталог: " + diagnostic.DirectoryPath, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(BindingPanel);
        var releaseButton = new Button { Content = "Завершить диагностическое чтение slow" };
        releaseButton.Click += (_, _) => diagnostic.ReleaseRead(); panel.Children.Add(releaseButton);
        panel.Children.Add(Notice);
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); Root.Children.Add(scroll);
        Title = "Speech Analyzer · диагностика привязки";
        UpdateCodexThread();
    }
    private async Task ReloadDiagnosticBindingAsync()
    {
        await codex.DisposeAsync();
        codex = new(binding: bindingDiagnostic!.Binding);
    }
    private void AddShellMessage(int number)
    {
        Chat.Add(new($"ИМИТАТОР · сообщение {number:00}", $"Синтетическая история {number:00}. Проверка сохранения позиции при новых сообщениях, смене темы и размера окна.\nСтрока с числами 42000 и русским текстом ёж 🦊.\nЭто локальный текст без вызова модели."));
    }
    private void ShellMessage_Click(object sender, RoutedEventArgs e)
    {
        if (bindingDiagnostic?.FullShell != true) return;
        AddShellMessage(Chat.Count + 1); OnNewMessage();
    }
    private void RestrictShellActions(DependencyObject parent)
    {
        // Сохраняем настоящую компоновку; разрешены только навигация, тема и локальная сверка.
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && button != LatestButton && button != ShellMessageButton
                && button != InspectChatBindingButton && button != AcceptChatBindingButton && button != ReplaceChatBindingButton)
                button.IsEnabled = false;
            if (child is ComboBox combo && combo != ThemeSelector) combo.IsEnabled = false;
            RestrictShellActions(child);
        }
    }
}
