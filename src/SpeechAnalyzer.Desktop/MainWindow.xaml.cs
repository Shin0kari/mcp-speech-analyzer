using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpeechAnalyzer.Core;

namespace SpeechAnalyzer.Desktop;

public partial class MainWindow : Window
{
    private readonly SystemClock clock = new();
    private readonly SimulatedTranscriptSource source;
    private readonly SimulatedModelBackend model = new();
    private readonly ConversationEngine engine;
    private readonly DispatcherTimer pulse;
    private readonly HashSet<Guid> seenMessages = [];
    private bool followChat = true;
    private bool closing;
    public ObservableCollection<ChatRow> Chat { get; } = [];
    public ObservableCollection<TranscriptRow> Transcript { get; } = [];

    public MainWindow()
    {
        source = new(clock);
        engine = new(clock, source, model, new MemoryConversationStore());
        InitializeComponent();
        DataContext = this;
        engine.Changed += Engine_Changed;
        pulse = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Pulse(), Dispatcher);
        Refresh();
    }

    private void Engine_Changed()
    {
        if (!closing && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
    }

    private void Configure()
    {
        source.DemoSpeechEnabled = DemoSpeech.IsChecked == true;
        model.Delay = TimeSpan.FromSeconds(DelaySelector.SelectedIndex switch { 1 => 8, 2 => 15, _ => 2 });
        engine.Configure(ContextInput.Text.Trim(), FallbackInput.Text.Trim());
    }

    private void Pulse()
    {
        Configure();
        engine.Pulse();
        // Флажок снимается после того, как имитатор потребил одноразовый сбой.
        if (FailNext.IsChecked == true && !model.FailNext) FailNext.IsChecked = false;
    }

    private void FailNext_Changed(object sender, RoutedEventArgs e) => model.FailNext = FailNext.IsChecked == true;

    private void Start_Click(object sender, RoutedEventArgs e) { Configure(); engine.Start(); }
    private void Pause_Click(object sender, RoutedEventArgs e) => engine.TogglePause();
    private void Stop_Click(object sender, RoutedEventArgs e) => engine.Stop();
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (engine is not null && IsInitialized) engine.SetMode(ModeSelector.SelectedIndex == 1 ? AnalysisMode.Automatic : AnalysisMode.Manual);
    }
    private void Answer_Click(object sender, RoutedEventArgs e) { Configure(); engine.Answer(); }
    private void Retry_Click(object sender, RoutedEventArgs e) => engine.Retry();
    private void Send_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserInput.Text)) { ShowNotice("Введите свой вопрос или инструкцию."); return; }
        Configure();
        if (engine.Answer(UserInput.Text.Trim())) { UserInput.Clear(); NoticeBorder.Visibility = Visibility.Collapsed; }
        else ShowNotice("Один пользовательский запрос уже ожидает обработки. Текст сохранён в поле; отправьте его после завершения текущего запроса.");
    }
    private void Inject_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SpeechInput.Text)) return;
        if (source.Status != CaptureStatus.Capturing) { ShowNotice("Для добавления реплики сначала запустите или продолжите симуляцию."); return; }
        source.Inject(SpeechInput.Text.Trim(), ChannelSelector.SelectedIndex == 0 ? AudioChannel.You : AudioChannel.Other,
            MeaningSelector.SelectedIndex == 1 ? SegmentMeaning.Question : SegmentMeaning.Fact);
        SpeechInput.Clear();
        engine.Pulse();
    }
    private void ShowNotice(string text) { NoticeText.Text = text; NoticeBorder.Visibility = Visibility.Visible; }

    private void Refresh()
    {
        if (closing) return;
        var snapshot = engine.Snapshot();
        StartButton.IsEnabled = snapshot.Capture == CaptureStatus.Stopped;
        PauseButton.IsEnabled = StopButton.IsEnabled = snapshot.Capture != CaptureStatus.Stopped;
        PauseButton.Content = snapshot.Capture == CaptureStatus.Paused ? "Продолжить" : "Пауза";
        InjectButton.IsEnabled = snapshot.Capture == CaptureStatus.Capturing;
        AnswerButton.IsEnabled = snapshot.CaptureBoundaryMs > 0;
        RetryButton.Visibility = snapshot.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = snapshot.Error is { } error ? $"Ошибка: {error}" : snapshot.Activity + $" · в очереди: {snapshot.PendingOperations}";
        CountdownText.Text = snapshot.NextAnalysisIn is { } remaining
            ? $"Следующий анализ через {Math.Ceiling(remaining.TotalSeconds):0} с" : "Таймер не запущен";
        BoundariesText.Text = $"Захвачено: {Position(snapshot.CaptureBoundaryMs)}\nОкончательный текст: {Position(snapshot.FinalizedThroughMs)}\nУспешно сохранён анализ: {Position(snapshot.ProcessedThroughMs)}\nВызовов имитатора: {model.CallCount}";
        QuestionsText.Text = snapshot.Conversation.State.Questions.Count == 0 ? "Вопросов пока нет." :
            string.Join("\n\n", snapshot.Conversation.State.Questions.Select(q => $"{QuestionLabel(q.Status)} · {q.Text}"));
        FactsText.Text = string.Join("\n", snapshot.Conversation.State.Facts.Concat(snapshot.Conversation.State.Commitments).Select(f => "• " + f));
        foreach (var message in snapshot.Conversation.Messages)
            if (seenMessages.Add(message.OperationId))
            {
                Chat.Add(new(message.CreatedAt.ToLocalTime().ToString("HH:mm:ss") + " · имитатор", message.UserInstruction is null
                    ? message.Text : $"Ваш запрос: {message.UserInstruction}\n\n{message.Text}"));
                if (followChat) Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Loaded);
                else LatestButton.Visibility = Visibility.Visible;
            }
        var segments = source.Segments;
        // Стабильные строки: финализация не пересоздаёт весь список и не сбрасывает прокрутку.
        foreach (var segment in segments)
        {
            var row = Transcript.FirstOrDefault(r => r.Id == segment.Id);
            string header = $"{Position(segment.StartMs)} — {Position(segment.EndMs)} · {(segment.Channel == AudioChannel.You ? "Вы" : "Собеседник")} · {(segment.IsFinal ? "окончательно" : "предварительно…")}";
            if (row is null) Transcript.Add(new(segment.Id, header, segment.Text));
            else row.Header = header;
        }
        YouLevel.Value = source.Status == CaptureStatus.Capturing && segments.Any(s => !s.IsFinal && s.Channel == AudioChannel.You) ? 0.65 : 0;
        OtherLevel.Value = source.Status == CaptureStatus.Capturing && segments.Any(s => !s.IsFinal && s.Channel == AudioChannel.Other) ? 0.55 : 0;
    }

    private void Chat_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            followChat = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 20;
            if (followChat) LatestButton.Visibility = Visibility.Collapsed;
        }
    }
    private void Latest_Click(object sender, RoutedEventArgs e) { followChat = true; ChatScroll.ScrollToEnd(); LatestButton.Visibility = Visibility.Collapsed; }
    private static string Position(long ms) => TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss\.fff");
    private static string QuestionLabel(QuestionStatus status) => status switch
    {
        QuestionStatus.Open => "Открыт", QuestionStatus.AnsweredByParticipant => "Ответил участник",
        QuestionStatus.Displayed => "Показан помощником", _ => "Неактуален"
    };

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!closing)
        {
            e.Cancel = true;
            closing = true;
            pulse.Stop();
            engine.Changed -= Engine_Changed;
            await engine.DisposeAsync();
            // DisposeAsync может завершиться синхронно. Закрываем на следующем
            // обороте Dispatcher, чтобы не вызывать Close повторно внутри OnClosing.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        base.OnClosing(e);
    }
}

public sealed record ChatRow(string Header, string Text);
public sealed class TranscriptRow(Guid id, string header, string text) : INotifyPropertyChanged
{
    public Guid Id { get; } = id;
    public string Text { get; } = text;
    private string headerValue = header;
    public string Header
    {
        get => headerValue;
        set { if (headerValue != value) { headerValue = value; PropertyChanged?.Invoke(this, new(nameof(Header))); } }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
