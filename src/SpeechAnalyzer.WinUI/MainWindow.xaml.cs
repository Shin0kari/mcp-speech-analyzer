using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpeechAnalyzer.Core;
using SpeechAnalyzer.Audio.Windows;
using Windows.Storage.Pickers;

namespace SpeechAnalyzer.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly SystemClock clock = new();
    private ITranscriptSource source;
    private SimulatedTranscriptSource? simulation;
    private readonly SimulatedModelBackend model = new();
    private ConversationEngine engine;
    private SessionCoordinator session;
    private WindowsCaptureLifecycle? audioCapture;
    private ProcessAudioTranscriber? transcriber;
    private readonly bool experimentalCudaStt = Environment.GetCommandLineArgs().Any(a => a is "--experimental-cuda-stt" or "--gpu-stt-v1");
    private AudioTimelineAdapter? audioTimeline;
    private SessionJournal? journal;
    private FileStream? sessionOwner;
    private string selectionKey = "simulation";
    private bool preparing, reportingFailure, shutdownAttempted;
    private readonly ContextBundle context = new();
    private CodexContextSession codex = new();
    private ApplicationRetrievalBackend? retrievalBackend;
    private readonly LocalControlHost control;
    private readonly SessionControlDispatcher controlDispatcher;
    private readonly DispatcherQueueTimer pulse;
    private readonly HashSet<Guid> seenMessages = [];
    private bool initialized, closing, transferring, followChat = true, chatScrollPending;
    private string preparedContext = "";
    private bool starting, loadingReasoning;
    private readonly BindingCandidateReview bindingReview = new();
    private readonly BindingUiDiagnostic? bindingDiagnostic = BindingUiDiagnostic.FromArguments();
    private WindowsSourceCatalog? sourceCatalog;
    private AudioSourceSelection? audioSelection;
    private TranscriptQueries transcriptQueries = null!;
    private TranscriptQuery? visibleTranscriptQuery;
    private string? visibleTranscriptCursor;
    private bool updatingSources;
    private string? lastAnalysisNotice;
    private const string SharedChannelContext = "Канал «Собеседник» может содержать преподавателя и другого студента. Используй только текстовые свидетельства ролей, сохраняй неопределённость авторства. Не приписывай слова и обязательства другого студента пользователю. Акустического различения голосов и диаризации нет.";
    private IReadOnlyList<AudioSourceEntry> sourceEntries = [];
    private long audioCatalogVersion;
    private string? pendingAudioSessionDirectory;
    private static readonly AudioSourceEntry NotSelectedSource = new("", "none", "Не выбрано");
    private readonly CancellationTokenSource codexLifetime = new();
    private readonly Windows.UI.ViewManagement.UISettings systemUiSettings = new();
    private readonly Microsoft.UI.System.ThemeSettings accessibilitySettings;
    private ThemePreference themePreference;
    private readonly string? acceptanceRoot = Environment.GetEnvironmentVariable("SPEECH_ANALYZER_ACCEPTANCE_ROOT");
    private string? AcceptanceSessionsDirectory => acceptanceRoot is null ? null : Path.Combine(acceptanceRoot, "conversations");
    private ConversationStoragePreferences storagePreferences = new();
    private AudioDiagnosticJournal? sessionDiagnostics;
    private string PreferencesDirectory => bindingDiagnostic is { } diagnostic ? Path.Combine(diagnostic.DirectoryPath, "preferences") : acceptanceRoot is { } root
        ? Path.Combine(root, "preferences") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeechAnalyzer");
    private string SessionsDirectory => AcceptanceSessionsDirectory ?? storagePreferences.ResolveDirectory();
    private string LegacySessionsDirectory => Path.Combine(PreferencesDirectory, "sessions");
    private string StoragePreferencesPath => Path.Combine(PreferencesDirectory, "conversation-storage.json");
    private string AppearancePath => Path.Combine(PreferencesDirectory, "appearance.json");
    private string SttModelPreferencesPath => Path.Combine(PreferencesDirectory, "stt-model.json");
    private string persistedSttModelPath = "";
    public ObservableCollection<ChatRow> Chat { get; } = [];
    private readonly TranscriptProjection transcriptProjection = new();
    public ObservableCollection<TranscriptRow> Transcript => transcriptProjection.Rows;
    public ObservableCollection<ContextFileRow> ContextFiles { get; } = [];

    public MainWindow()
    {
        source = simulation = new(clock);
        engine = new(clock, source, model, new MemoryConversationStore());
        session = new(engine, source, duration: new(clock));
        transcriptQueries = new(() => source.Segments, clock);
        InitializeComponent();
        RetainTestAudio.IsChecked=Environment.GetCommandLineArgs().Contains("--test-session-recording",StringComparer.Ordinal);
        accessibilitySettings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id);
        Root.DataContext = this;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);
        Root.Loaded += (_, _) =>
        {
            UpdateMinimumWindowSize();
            Root.XamlRoot.Changed += (_, _) => UpdateMinimumWindowSize();
        };
        Root.ActualThemeChanged += (_, _) => UpdateThemeColors();
        systemUiSettings.ColorValuesChanged += SystemColorsChanged;
        accessibilitySettings.Changed += HighContrastChanged;
        try { themePreference = AppearancePreferences.Load(AppearancePath).Theme; }
        catch (Exception error) { ShowNotice("Не удалось прочитать тему: " + error.Message); }
        try { storagePreferences = ConversationStoragePreferences.Load(StoragePreferencesPath); }
        catch (Exception error) { ShowNotice("Не удалось прочитать настройки хранения: " + error.Message); }
        ThemeSelector.SelectedIndex = (int)themePreference;
        ApplyTheme();
        ReasoningSelector.ItemsSource = new[] { new ReasoningOption("", "По умолчанию в Codex") };
        ReasoningSelector.SelectedIndex = 0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        session.Changed += Engine_Changed;
        controlDispatcher = new(() => session, () => transcriptQueries, ControlStatus,
            () =>
            {
                RefreshSources(true);
                if (audioSelection is null) throw new IOException("Каталог источников недоступен.");
                return Task.FromResult<object>(new { catalogVersion = audioSelection.Status().CatalogVersion,
                    sources = sourceEntries, selection = audioSelection.Status(), captureScope = "process_tree" });
            },
            async (microphone, window, policy, version) =>
            {
                await SelectAudioSourcesAsync(microphone, window, policy, version);
                CaptureMode.SelectedIndex = 1;
                return new { selected = audioSelection!.Status(), captureStarted = false };
            },
            async text => { ContextInput.Text = text; await Configure(); }, StartCaptureAsync, ResumeCaptureAsync);
        control = new(bindingDiagnostic is null ? DispatchControl : (_, _) => throw new InvalidOperationException("Диагностика привязки: MCP-команды выключены."),
            bindingDiagnostic is null ? null : "speech-binding-ui-" + Guid.NewGuid().ToString("N"));
        McpText.Text = $"Pipe: {control.PipeName}\nЗапуск MCP не включает захват. Сервер: {FindMcpServerPath()}";
        pulse = DispatcherQueue.CreateTimer();
        pulse.Interval = TimeSpan.FromMilliseconds(100);
        pulse.Tick += (_, _) => Pulse();
        AppWindow.Closing += Closing;
        _ = ObserveControlReadyAsync();
        initialized = true;
        try
        {
            ModelPathInput.Text = Environment.GetEnvironmentVariable("SPEECH_ANALYZER_STT_MODEL")
                ?? SttModelPreferences.Load(SttModelPreferencesPath).ResolveModelPath(FindModelPath);
            if (ModelPathInput.Text.Length > 0 && !File.Exists(ModelPathInput.Text))
                ShowNotice("Сохранённая модель Whisper не найдена. Выберите файл; модель автоматически не заменена.");
        }
        catch (Exception error) { ModelPathInput.Text = ""; ShowNotice("Не удалось прочитать выбор STT-модели: " + error.Message); }
        persistedSttModelPath = ModelPathInput.Text;
        VadPathInput.Text = FindLocalModelPath("ggml-silero-v6.2.0.bin");
        if (bindingDiagnostic is null)
        {
            RefreshSources(); RefreshSavedSessions(); _ = RunUiAsync(Configure);
        }
        else InitializeBindingDiagnostic();
        Refresh();
        pulse.Start();
    }

    private void UpdateMinimumWindowSize()
    {
        // Двухколоночная оболочка требует места для панели управления и действий вкладок.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? 1;
            bool minimumChanged = presenter.PreferredMinimumWidth != (int)Math.Ceiling(1000 * scale);
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(1000 * scale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(560 * scale);
            // При росте DPI минимум увеличивает окно; возвращаем его целиком в рабочую область.
            if (minimumChanged && presenter.State == OverlappedPresenterState.Restored)
            {
                var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                var area = display.WorkArea;
                area.X += display.OuterBounds.X; area.Y += display.OuterBounds.Y;
                var position = AppWindow.Position;
                int x = Math.Clamp(position.X, area.X, Math.Max(area.X, area.X + area.Width - AppWindow.Size.Width));
                int y = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - AppWindow.Size.Height));
                if (x != position.X || y != position.Y) AppWindow.Move(new(x, y));
            }
            if (bindingDiagnostic?.FullShell == true)
                File.WriteAllText(Path.Combine(bindingDiagnostic.DirectoryPath, "visual-state.json"),
                    JsonSerializer.Serialize(new { scale, width = AppWindow.Size.Width, height = AppWindow.Size.Height,
                        minimumWidth = presenter.PreferredMinimumWidth, minimumHeight = presenter.PreferredMinimumHeight }));
        }
    }

    private void SystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    { if (!closing) ApplyTheme(); });
    private void HighContrastChanged(Microsoft.UI.System.ThemeSettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    { if (!closing) UpdateThemeColors(); });
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        try
        {
            var selected = (ThemePreference)ThemeSelector.SelectedIndex;
            new AppearancePreferences(selected).Save(AppearancePath);
            themePreference = selected; ApplyTheme();
        }
        catch (Exception error) { ThemeSelector.SelectedIndex = (int)themePreference; ShowNotice("Тема не сохранена: " + error.Message); }
    }
    private void ApplyTheme()
    {
        var foreground = systemUiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
        bool systemDark = 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128;
        Root.RequestedTheme = themePreference switch
        {
            ThemePreference.Dark => ElementTheme.Dark, ThemePreference.Light => ElementTheme.Light,
            _ => systemDark ? ElementTheme.Dark : ElementTheme.Light
        };
        UpdateThemeColors();
    }
    private void UpdateThemeColors()
    {
        var bar = AppWindow.TitleBar;
        bool highContrast = accessibilitySettings.HighContrast;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        Windows.UI.Color Color(byte value) => Windows.UI.Color.FromArgb(255, value, value, value);
        var background = highContrast ? systemUiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background) : Color(dark ? (byte)32 : (byte)243);
        var foreground = highContrast ? systemUiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground) : Color(dark ? (byte)255 : (byte)25);
        bar.BackgroundColor = bar.InactiveBackgroundColor = bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = background;
        bar.ForegroundColor = bar.ButtonForegroundColor = foreground;
        bar.InactiveForegroundColor = bar.ButtonInactiveForegroundColor = highContrast ? foreground : Color(dark ? (byte)170 : (byte)95);
        bar.ButtonHoverBackgroundColor = highContrast ? systemUiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent) : Color(dark ? (byte)55 : (byte)225);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = highContrast ? background : Color(dark ? (byte)70 : (byte)210);
        bar.ButtonPressedForegroundColor = foreground;
        if (audioSelection is not null) SynchronizeSourceControls();
    }
    private void RefreshSavedSessions()
    {
        try
        {
            ConversationRootText.Text = "Новые разговоры: " + SessionsDirectory + ". Смена папки не переносит существующие файлы.";
            SavedSessionsSelector.ItemsSource = AcceptanceSessionsDirectory is { } isolated
                ? ConversationFiles.List(isolated) : ConversationFiles.ListRoots([.. storagePreferences.KnownDirectories, LegacySessionsDirectory]);
        }
        catch (Exception error) { ShowNotice("Список разговоров недоступен: " + error.Message); }
    }
    private void RefreshSessions_Click(object sender, RoutedEventArgs e) => RefreshSavedSessions();
    private async void ChooseConversationRoot_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (acceptanceRoot is not null)
            throw new InvalidOperationException("Для тестовой сборки задан отдельный каталог разговоров при запуске.");
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var selected = await picker.PickSingleFolderAsync();
        if (selected is null) return;
        if (starting || preparing || transferring || loadingReasoning) throw new InvalidOperationException("Дождитесь текущей операции.");
        var next = storagePreferences.WithRoot(selected.Path);
        next.Save(StoragePreferencesPath); storagePreferences = next; pendingAudioSessionDirectory = null; RefreshSavedSessions();
    });
    private async void DefaultConversationRoot_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() =>
    {
        if (starting || preparing || transferring || loadingReasoning) throw new InvalidOperationException("Дождитесь текущей операции.");
        if (acceptanceRoot is not null)
            throw new InvalidOperationException("Для тестовой сборки задан отдельный каталог разговоров при запуске.");
        var next = storagePreferences.WithRoot(null);
        next.Save(StoragePreferencesPath); storagePreferences = next; pendingAudioSessionDirectory = null; RefreshSavedSessions();
        return Task.CompletedTask;
    });
    private void SavedSession_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SavedSessionsSelector.SelectedItem is SavedConversation item) SessionPathInput.Text = item.DirectoryPath;
    }
    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        string directory = journal?.DirectoryPath ?? throw new InvalidOperationException("Сначала откройте сохранённый разговор.");
        if (!Directory.Exists(directory)) throw new IOException("Папка разговора недоступна.");
        if (!await Windows.System.Launcher.LaunchFolderPathAsync(directory)) throw new IOException("Windows не открыла папку разговора.");
    });
    private async void ExportTranscript_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        var current = journal ?? throw new InvalidOperationException("Симуляция не сохранена. Откройте разговор с журналом.");
        var duration = session.Duration.Snapshot();
        string path;
        try { path = await Task.Run(() => ConversationFiles.Export(current, duration)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException("Не удалось сохранить TXT-снимок. Экспорт не меняет исходный журнал. " + error.Message, error); }
        ShowNotice("Сохранён полный окончательный текст: " + path + ". Новая речь потребует повторного экспорта.");
    });
    private async void NewSession_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (session.Phase != SessionPhase.Stopped || starting || preparing || transferring || loadingReasoning)
            throw new InvalidOperationException("Перед новым разговором завершите остановку и текущие операции.");
        string directory = Path.Combine(SessionsDirectory, Guid.NewGuid().ToString("N"));
        // Новый разговор получает только последний успешно использованный микрофон.
        var preferences = AudioGlobalPreferences.Load(GlobalAudioPreferencesPath());
        Directory.CreateDirectory(directory);
        AudioSelectionFile.Save(Path.Combine(directory, "audio-sources.json"), new(1, preferences.LastSuccessfulMicrophoneDeviceId, null));
        await PrepareSessionAsync(directory);
    });

    private async Task ObserveControlReadyAsync()
    {
        try { await control.Ready; }
        catch (Exception error) { if (!closing) { McpText.Text = "MCP недоступен: " + error.Message; ShowNotice("Не удалось открыть локальный канал управления."); } }
    }
    private void Engine_Changed() { if (!closing) DispatcherQueue.TryEnqueue(Refresh); }
    private async Task Configure()
    {
        string text = ContextInput.Text.Trim() + "\n\n" + preparedContext + "\n\n" + SharedChannelContext;
        var currentJournal = journal;
        try { await session.ConfigureAsync(text, FallbackInput.Text.Trim(), currentJournal is null ? null : () => Task.Run(() =>
            { currentJournal.SaveContext(text, null); if(context.Files.Count>0||context.ProjectRoot is not null)context.SaveManifest(Path.Combine(currentJournal.DirectoryPath,"context-manifest.json")); })); }
        catch
        {
            if (currentJournal is not null && !preparing && session.Phase == SessionPhase.Capturing)
                ShowNotice("Не удалось сохранить контекст. Исправная аудиозапись продолжается.");
            throw;
        }
    }
    private ModelOperationVersions CaptureOperationVersions()
    {
        string contextSnapshot = ContextInput.Text.Trim() + "\n\n" + preparedContext + "\n\n" + SharedChannelContext;
        string attachments = JsonSerializer.Serialize(context.Files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new { file.Name, file.Sha256 }));
        string settings = JsonSerializer.Serialize(new { model = CodexModelSettings.Model, reasoningEffort = codex.ReasoningEffort,
            originalsRetrieval = "application-v1",
            mode = ModeSelector.SelectedIndex });
        return new(OperationVersionHash.Of(JsonSerializer.Serialize(new { context = contextSnapshot, fallback = FallbackInput.Text.Trim() })),
            OperationVersionHash.Of(attachments), OperationVersionHash.Of(settings), codex.ReasoningEffort);
    }
    private async Task RunUiAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { if (!closing) ShowNotice(error.Message); }
    }
    private static string FindModelPath() => FindLocalModelPath("ggml-base-q5_1.bin");
    private static string FindMcpServerPath()
    {
        // Комплект должен показывать собственный сервер, а не путь из рабочей копии.
        string packaged = Path.Combine(AppContext.BaseDirectory, "mcp", "speech-analyzer-mcp.exe");
        if (File.Exists(packaged)) return packaged;
        for (string? path = AppContext.BaseDirectory; path is not null; path = Directory.GetParent(path)?.FullName)
        {
            string candidate = Path.Combine(path, "mcp", "target", "debug", "speech-analyzer-mcp.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "не найден; соберите или укажите отдельный MCP-сервер";
    }
    private static string FindLocalModelPath(string fileName)
    {
        for (string? path = AppContext.BaseDirectory; path is not null; path = Directory.GetParent(path)?.FullName)
        {
            string candidate = Path.Combine(path, "models", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }
    private void PersistSelectedSttModel()
    {
        if (ModelPathInput.Text == persistedSttModelPath) return;
        var preferences = SttModelPreferences.Select(ModelPathInput.Text);
        preferences.Save(SttModelPreferencesPath);
        persistedSttModelPath = preferences.ModelPath!;
        ModelPathInput.Text = persistedSttModelPath;
    }
    private void ModelPathInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        try { PersistSelectedSttModel(); }
        catch (Exception error) { ShowNotice("Не удалось сохранить выбор STT-модели: " + error.Message); }
    }
    private void RefreshSources(bool fail = false)
    {
        try
        {
            if (audioSelection is null)
            {
                sourceCatalog ??= new();
                string preferencesPath = GlobalAudioPreferencesPath();
                var preferences = AudioGlobalPreferences.Load(preferencesPath);
                audioSelection = new(sourceCatalog, new(1, preferences.LastSuccessfulMicrophoneDeviceId, null), SavePendingAudioSettings);
            }
            sourceEntries = audioSelection.Refresh();
            audioCatalogVersion = audioSelection.Status().CatalogVersion;
            SynchronizeSourceControls();
        }
        catch (Exception error) { ShowNotice("Перечень источников недоступен: " + error.Message); if (fail) throw; }
    }
    private void SynchronizeSourceControls()
    {
        if (audioSelection is null) return;
        updatingSources = true;
        try
        {
            var status = audioSelection.Status();
            var micChoices = new[] { NotSelectedSource }.Concat(sourceEntries.Where(s => s.Kind == "microphone")).ToArray();
            var windowChoices = new[] { NotSelectedSource }.Concat(sourceEntries.Where(s => s.Kind == "application_window")).ToArray();
            MicrophoneSelector.ItemsSource = micChoices;
            ProcessSelector.ItemsSource = windowChoices;
            MicrophoneSelector.SelectedItem = micChoices.FirstOrDefault(s => s.SourceId == status.MicrophoneSourceId) ?? NotSelectedSource;
            ProcessSelector.SelectedItem = windowChoices.FirstOrDefault(s => s.SourceId == status.WindowSourceId) ?? NotSelectedSource;
            var tint = new Microsoft.UI.Xaml.Media.SolidColorBrush(accessibilitySettings.HighContrast
                ? systemUiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background)
                : Root.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 56, 54, 44) : Windows.UI.Color.FromArgb(255, 255, 248, 228));
            MicrophoneSelector.Background = status.MicrophoneAvailability == "available" ? null : tint;
            ProcessSelector.Background = status.Availability == "available" ? null : tint;
            WindowPolicySelector.SelectedIndex = status.Selected.Window?.Policy == WindowMatchPolicy.Application ? 1 : 0;
            AudioSourceStatusText.Text = $"Окно: {SourceAvailability(status.Availability)}; микрофон: {SourceAvailability(status.MicrophoneAvailability)}. {status.Label}";
            RequiredSourcesText.Text = status.Availability == "available" && status.MicrophoneAvailability == "available" ? "" :
                $"Для реального захвата выберите микрофон и приложение. Не выбрано: {string.Join(" и ", new[] { status.MicrophoneAvailability == "available" ? null : "микрофон", status.Availability == "available" ? null : "приложение" }.Where(x => x is not null))}.";
        }
        finally { updatingSources = false; }
    }
    private static string SourceAvailability(string value) => value switch
    {
        "available" => "доступен", "unavailable" => "недоступен — обновите список", "ambiguous" => "несколько совпадений — выберите окно",
        _ => "не выбран"
    };
    private async Task SelectAudioSourcesAsync(string? microphoneId, string? windowId, WindowMatchPolicy policy, long? catalogVersion = null, AudioSelectionChange change = AudioSelectionChange.Both)
    {
        if (starting || preparing) throw new InvalidOperationException("Дождитесь завершения старта.");
        await session.SelectAudioSourcesAsync(audioSelection ?? throw new IOException("Каталог источников недоступен."), microphoneId, windowId, policy, catalogVersion, change);
        SynchronizeSourceControls();
    }
    private async void AudioSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updatingSources || audioSelection is null) return;
        var change = ReferenceEquals(sender, MicrophoneSelector) ? AudioSelectionChange.Microphone
            : ReferenceEquals(sender, ProcessSelector) ? AudioSelectionChange.Window : AudioSelectionChange.WindowPolicy;
        if (MicrophoneSelector.SelectedItem is AudioSourceEntry mic && ProcessSelector.SelectedItem is AudioSourceEntry window)
            await RunUiAsync(async () =>
            {
                try { await SelectAudioSourcesAsync(mic.SourceId.Length == 0 ? null : mic.SourceId, window.SourceId.Length == 0 ? null : window.SourceId, WindowPolicySelector.SelectedIndex == 1 ? WindowMatchPolicy.Application : WindowMatchPolicy.SpecificWindow, audioCatalogVersion, change); }
                catch (StaleAudioCatalogException) { RefreshSources(); }
                catch { SynchronizeSourceControls(); throw; }
            });
    }
    private void RefreshSources_Click(object sender, RoutedEventArgs e) { if (session.Phase == SessionPhase.Stopped) RefreshSources(); }
    private string GlobalAudioPreferencesPath() => Path.Combine(PreferencesDirectory, "audio-preferences.json");
    private void SavePendingAudioSettings(AudioSelectionSettings settings)
    {
        pendingAudioSessionDirectory ??= Path.Combine(SessionsDirectory, Guid.NewGuid().ToString("N"));
        AudioSelectionFile.Save(Path.Combine(pendingAudioSessionDirectory, "audio-sources.json"), settings);
    }
    private void BindSessionAudioSettings(string directory, AudioSelectionSettings settings)
    {
        string path = Path.Combine(directory, "audio-sources.json");
        audioSelection = new(sourceCatalog ??= new(), settings, value => AudioSelectionFile.Save(path, value));
        sourceEntries = audioSelection.Refresh();
        audioCatalogVersion = audioSelection.Status().CatalogVersion;
        SynchronizeSourceControls();
    }
    internal static CodexSessionSettings ResolveAcceptanceCodexSettings(CodexSessionSettings? saved)
        => new(saved?.ThreadId, "low");

    private async Task PrepareSessionAsync(string? restoreDirectory = null)
    {
        if (transferring || loadingReasoning) throw new InvalidOperationException("Дождитесь завершения операции Codex.");
        if (restoreDirectory is not null && AcceptanceSessionsDirectory is { } isolated &&
            (!string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(restoreDirectory))),
                isolated, StringComparison.OrdinalIgnoreCase) ||
             (File.GetAttributes(restoreDirectory) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("Откройте разговор из отдельного каталога этой тестовой сборки.");
        bool real = CaptureMode.SelectedIndex == 1 || restoreDirectory is not null;
        string? vadPath = VadToggle.IsChecked == true ? VadPathInput.Text.Trim() : null;
        if (real && restoreDirectory is null && RetainTestAudio.IsChecked!=true)
        {
            if (!File.Exists(ModelPathInput.Text)) throw new IOException("Выберите существующую локальную модель Whisper.");
            if (vadPath is not null && !File.Exists(vadPath)) throw new IOException("Выберите существующую локальную модель Silero VAD или выключите экспериментальный VAD.");
        }
        string key = real ? $"real|{ModelPathInput.Text}|{vadPath}|retain={RetainTestAudio.IsChecked==true}" : "simulation";
        if (key == selectionKey && restoreDirectory is null) return;
        if (preparing || session.Phase != SessionPhase.Stopped) throw new InvalidOperationException("Сменить источник можно только после завершённой остановки.");
        if (real && restoreDirectory is null)
        {
            if(RetainTestAudio.IsChecked!=true || File.Exists(ModelPathInput.Text))PersistSelectedSttModel();
            key = $"real|{ModelPathInput.Text}|{vadPath}|retain={RetainTestAudio.IsChecked==true}";
        }
        string? existingSessionDirectory = real ? journal?.DirectoryPath : null;
        long previousBoundary = real && journal is not null && restoreDirectory is null ? source.CaptureBoundaryMs : 0;
        preparing = true;
        try
        {
            session.Changed -= Engine_Changed;
            await session.DisposeAsync();
            if (transcriber is not null) await transcriber.DisposeAsync();
            if (sessionDiagnostics is not null) await sessionDiagnostics.DisposeAsync();
            sessionDiagnostics = null;
            sessionOwner?.Dispose(); sessionOwner = null;
            audioCapture = null; audioTimeline = null; transcriber = null; journal = null; retrievalBackend = null;
            lastAnalysisNotice = null;
            if (real)
            {
                string directory = restoreDirectory ?? existingSessionDirectory ?? pendingAudioSessionDirectory ?? Path.Combine(SessionsDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var selectedForSession = restoreDirectory is null ? audioSelection!.Status().Selected :
                    AudioSelectionFile.LoadForSession(Path.Combine(directory, "audio-sources.json"));
                BindSessionAudioSettings(directory, selectedForSession);
                if (!File.Exists(Path.Combine(directory, "audio-sources.json")))
                    AudioSelectionFile.Save(Path.Combine(directory, "audio-sources.json"), selectedForSession);
                if (restoreDirectory is null) pendingAudioSessionDirectory = null;
                sessionOwner = new(Path.Combine(directory, ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                journal = new(directory);
                SessionEventLog.Current = new(directory);
                SessionEventLog.Current.Write("conversation_open",new{directory,retainedAudioEnabled=RetainTestAudio.IsChecked==true});
                Notice.IsOpen = false;
                bindingReview.Invalidate(); ChatBindingEvidenceText.Text = "";
                if (journal.RecoveryNotice is { } recoveryNotice) ShowNotice(recoveryNotice);
                var codexSettings = restoreDirectory is not null ? journal.LoadCodexSettings() : new CodexSessionSettings(codex.ThreadId, codex.ReasoningEffort);
                if (acceptanceRoot is not null)
                    codexSettings = ResolveAcceptanceCodexSettings(codexSettings);
                await codex.DisposeAsync();
                codex = new(codexSettings, new CodexThreadBinding(directory));
                var sttConfiguration = SttApplicationConfiguration.Resolve(AppContext.BaseDirectory, Environment.GetCommandLineArgs(),
                    Environment.GetEnvironmentVariable);
                transcriber = sttConfiguration.Create(ModelPathInput.Text, vadPath);
                var savedDuration = CaptureDuration.Load(directory);
                long restoredAudioBoundary = Math.Max(savedDuration?.FinalizedAudioBoundaryMs ?? 0,
                    Math.Max(previousBoundary, journal.Read().State.ProcessedThroughMs));
                var diagnostics=new AudioDiagnosticJournal(directory);
                sessionDiagnostics = diagnostics;
                var buffered = new BufferedTranscriptSource(clock, transcriber, journal.Transcript, restoredAudioBoundary, chunkSeconds: sttConfiguration.CandidateInput ? SttInputLimits.MainSeconds : 8,
                    diagnostics: new SttDiagnostics(diagnostics),
                    recoveryStore: Environment.GetCommandLineArgs().Contains("--pre-gap-drain", StringComparer.Ordinal) ? journal : null,
                    quantizationFloorGuard: Environment.GetCommandLineArgs().Contains("--pcm16-floor-guard", StringComparer.Ordinal));
                buffered.Finalized += journal.AppendTranscript;
                source = buffered; simulation = null;
                // Снимок ограничен кольцом адаптера и сохраняется также без режима Measure.
                audioTimeline = new(buffered, clock, diagnostic =>
                {
                    diagnostics.TryWrite("audio_timeline_snapshot",diagnostic);
                });
                var recording = RetainTestAudio.IsChecked==true ? new TestSessionAudioRecorder(directory, SessionEventLog.Current,
                    Math.Max(restoredAudioBoundary*16,(savedDuration?.ActiveCaptureDurationMs??0)*16)) : null;
                // Writer живёт до Dispose transcriber, чтобы сохранить выход idle worker.
                audioCapture = new(audioTimeline, audioSelection!, recording, diagnostics, ownsDiagnostics: false);
                audioCapture.Failed += ReportAudioFailure;
                audioCapture.AnalysisFailed += error => DispatcherQueue.TryEnqueue(()=>ShowAnalysisFailure(error));
                retrievalBackend = ApplicationRetrievalBackend.CreateDesktop(
                    new CodexRetrievalModelClient(() => codex, CodexDirectory), journal);
                engine = new(clock, source, retrievalBackend, journal, CaptureOperationVersions, "GPT-6.1 Terra",
                    sttConfiguration.CandidateInput ? journal.BindRollingContext(() => RollingContextConfiguration.Parse(
                        Environment.GetEnvironmentVariable(RollingContextConfiguration.EnvironmentKey))) : null,
                    deferRecoveredOperations: true);
                if (savedDuration is null && journal.Transcript.Count > 0)
                    savedDuration = new(DurationUncertain: true, TranscriptMayBeIncomplete: true);
                // Пустой новый разговор тоже появляется в каталоге, без старта записи.
                if (!File.Exists(Path.Combine(directory, "capture-duration.json")))
                    CaptureDuration.Save(directory, savedDuration ?? new());
                var duration = new CaptureDuration(clock, savedDuration, value => CaptureDuration.Save(directory, value));
                session = new(engine, source, audioCapture, duration: duration, independentRecording: recording is not null);
                if (restoreDirectory is not null)
                {
                    context.Clear(); ContextFiles.Clear(); preparedContext = ""; AttachmentsText.Text = "0 файлов";
                    ReasoningSelector.ItemsSource = new[] { new ReasoningOption(codex.ReasoningEffort ?? "", codex.ReasoningEffort ?? "По умолчанию в Codex") };
                    ReasoningSelector.SelectedIndex = 0;
                    UpdateCodexThread();
                    ContextInput.Text = journal.LoadContext()?.Text ?? "";
                }
                StorageText.Text = recording is not null ? "ТЕСТ: аудио сохраняется в " + Path.Combine(directory,"audio") + ". Отдельные WAV: Вы / Собеседник. Временный PCM STT ограничен по времени."
                    : "Полный текст: "+directory+". Сохранение аудио выключено.";
                SessionPathInput.Text = directory;
                RefreshSavedSessions();
                CaptureMode.SelectedIndex = 1;
            }
            else
            {
                SessionEventLog.Current=null;
                source = simulation = new(clock);
                engine = new(clock, source, model, new MemoryConversationStore());
                session = new(engine, source, duration: new(clock));
                StorageText.Text = "Симуляция хранится в памяти; аудио не захватывается.";
            }
            selectionKey = key;
            transcriptQueries = new(() => source.Segments, clock);
            visibleTranscriptQuery = null; visibleTranscriptCursor = null;
            Chat.Clear(); seenMessages.Clear(); transcriptProjection.Synchronize([]);
            session.Changed += Engine_Changed;
            CaptureBadge.Text = real ? $"РАСШИФРОВКА: ЛОКАЛЬНЫЙ WHISPER · БЛОКИ {(engine.RollingContextSeconds.HasValue ? SttInputLimits.MainSeconds : 8)} С" + (experimentalCudaStt ? " · CUDA: ЭКСПЕРИМЕНТ" : "")
                + (vadPath is null ? "" : " · VAD: ЭКСПЕРИМЕНТ") : "РАСШИФРОВКА: СИМУЛЯЦИЯ";
            ModeNotice.Message = real ? "Выбранные микрофон и дерево процесса захватываются только после Старт. Расшифровка сохраняется локально; анализ выполняет GPT-6.1 Terra через Codex и сохраняется в журнале операций." : "Демонстрационные реплики появляются раз в 6 секунд. Аудио не захватывается; ответы создаёт имитатор.";
            YouLevelLabel.Text = real ? "Вы · уровень микрофона" : "Вы · условный уровень";
            OtherLevelLabel.Text = real ? "Собеседник · уровень приложения" : "Собеседник · условный уровень";
            ConnectionText.Text = real ? "STT: локальный worker · Terra: Codex · текст/операции: на диске" : "Анализ: имитатор · хранение: в памяти";
            await session.SetModeAsync(ModeSelector.SelectedIndex == 1 ? AnalysisMode.Automatic : AnalysisMode.Manual);
            await Configure();
            engine.ResumeRecoveredOperations();
        }
        catch
        {
            await session.DisposeAsync();
            if (source is IAsyncDisposable disposable) await disposable.DisposeAsync();
            sessionOwner?.Dispose(); sessionOwner = null;
            // После ошибки подготовки оставляем управляемую остановленную симуляцию.
            if (transcriber is not null) await transcriber.DisposeAsync();
            journal = null; transcriber = null; audioCapture = null; audioTimeline = null; retrievalBackend = null;
            source = simulation = new(clock);
            engine = new(clock, source, model, new MemoryConversationStore());
            session = new(engine, source, duration: new(clock)); selectionKey = "simulation";
            session.Changed += Engine_Changed;
            CaptureMode.SelectedIndex = 0;
            CaptureBadge.Text = "РАСШИФРОВКА: СИМУЛЯЦИЯ";
            ConnectionText.Text = "Анализ: имитатор · хранение: в памяти";
            throw;
        }
        finally { preparing = false; Refresh(); }
    }
    private async void RestoreSession_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (!string.IsNullOrWhiteSpace(SessionPathInput.Text))
        {
            string directory = Path.GetFullPath(SessionPathInput.Text.Trim());
            if (!File.Exists(Path.Combine(directory, "transcript.jsonl")) && !File.Exists(Path.Combine(directory, "capture-duration.json")))
                throw new IOException("В каталоге нет журнала разговора или длительности захвата.");
            await PrepareSessionAsync(directory); return;
        }
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await PrepareSessionAsync(folder.Path);
    });
    private async void ReportAudioFailure(string error)
    {
        if (closing || reportingFailure || session.Phase == SessionPhase.Faulted) return;
        reportingFailure = true;
        SessionEventLog.Current?.Write("capture_error",new{error});
        try { await session.ReportFailureAsync(error); }
        catch { /* Ошибка видна в состоянии координатора. */ }
        finally { reportingFailure = false; }
    }
    private async void Context_Changed(object sender, TextChangedEventArgs e) { if (initialized) await RunUiAsync(Configure); }
    private void ShowAnalysisFailure(string error)
    {
        bool first = lastAnalysisNotice is null;
        lastAnalysisNotice ??= error;
        bool active = audioCapture?.RecordingStatus is { Accepting: true, Error: null };
        if (first) SessionEventLog.Current?.Write("analysis_error",new{error=lastAnalysisNotice, recordingActive=active});
        string message = SessionStatusPresentation.AnalysisFailure(lastAnalysisNotice, active);
        if (Notice.Message != message) ShowNotice(message);
    }
    private void Pulse()
    {
        if (bindingDiagnostic?.FullShell == true) { RestrictShellActions(MainLayout); return; }
        if (simulation is not null) simulation.DemoSpeechEnabled = DemoSpeech.IsChecked == true;
        audioCapture?.Pulse();
        if (source is BufferedTranscriptSource { Error: { } error })
        { if(audioCapture?.RecordingStatus is not null || session.RecordingStopped)ShowAnalysisFailure(error);else ReportAudioFailure(error); }
        model.Delay = TimeSpan.FromSeconds(DelaySelector.SelectedIndex switch { 1 => 8, 2 => 15, _ => 2 });
        try { session.Pulse(); }
        catch (Exception pulseError) { ShowAnalysisFailure("Ошибка обработки сессии: " + pulseError.Message); }
        if (FailNext.IsChecked == true && !model.FailNext) FailNext.IsChecked = false;
    }
    private void FailNext_Changed(object sender, RoutedEventArgs e) { if (initialized) model.FailNext = FailNext.IsChecked == true; }
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunUiAsync(StartCaptureAsync);
    private string CodexDirectory()
    {
        string directory = context.ProjectRoot ?? Path.Combine(PreferencesDirectory, "codex");
        Directory.CreateDirectory(directory);
        return directory;
    }
    private void UpdateCodexThread() => CodexThreadText.Text = codex.BindingRecoveryPending
        ? "Исход создания чата неизвестен. Укажите известный ID и проверьте соответствие; повторное создание заблокировано."
        : codex.ThreadName ?? "Рабочий чат Terra ещё не создан";
    private void ChatBindingId_Changed(object sender, TextChangedEventArgs e)
    {
        bindingReview.Invalidate();
        if (AcceptChatBindingButton is not null) AcceptChatBindingButton.IsEnabled = false;
        if (ChatBindingEvidenceText is not null) ChatBindingEvidenceText.Text = "";
    }
    private void RequireBindingRecovery()
    {
        if (session.Phase != SessionPhase.Stopped || starting || preparing || transferring || loadingReasoning)
            throw new InvalidOperationException("Остановите сессию и дождитесь текущей операции.");
        if (!codex.BindingRecoveryPending) throw new InvalidOperationException("Нет неопределённой привязки чата.");
    }
    private async void InspectChatBinding_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        RequireBindingRecovery();
        Notice.IsOpen = false;
        long revision = bindingReview.Invalidate();
        string candidateId = ChatBindingIdInput.Text.Trim();
        CodexBindingProbe.ValidateId(candidateId);
        ChatBindingEvidenceText.Text = "Проверка метаданных и истории…";
        loadingReasoning = true; Refresh();
        try
        {
            var evidence = bindingDiagnostic is null
                ? await codex.InspectBindingCandidateAsync(candidateId, CodexDirectory(), codexLifetime.Token)
                : await bindingDiagnostic.InspectAsync(candidateId);
            if (!bindingReview.Complete(revision, evidence))
            { ChatBindingEvidenceText.Text = "ID изменялся во время проверки. Сведения устарели; проверьте кандидата заново."; return; }
            var candidate = evidence.Candidate;
            ChatBindingEvidenceText.Text = $"ID: {candidate.ThreadId}\nКаталог совпал с уникальным намерением создания: {candidate.WorkingDirectory}\n" +
                $"Название: {candidate.Name ?? "отсутствует"}; модель: {candidate.Model ?? "не указана в метаданных"}; ходов: {candidate.Turns}.\n" +
                "Соответствие установлено по уникальному каталогу. Смысл содержимого истории автоматически не подтверждается. Привязка изменится только по кнопке принятия.";
        }
        catch { ChatBindingEvidenceText.Text = "Соответствие не подтверждено. Неопределённая привязка сохранена."; throw; }
        finally { loadingReasoning = false; Refresh(); }
    });
    private async void AcceptChatBinding_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        RequireBindingRecovery();
        var evidence = bindingReview.Evidence ?? throw new InvalidOperationException("Сначала проверьте кандидата.");
        if (evidence.Candidate.ThreadId != ChatBindingIdInput.Text.Trim()) throw new InvalidOperationException("ID кандидата изменился.");
        loadingReasoning = true; Refresh();
        try
        {
            if (bindingDiagnostic is null) await codex.AcceptBindingCandidateAsync(evidence, codexLifetime.Token);
            else { bindingDiagnostic.Binding.AcceptCandidate(evidence); await ReloadDiagnosticBindingAsync(); }
            bindingReview.Invalidate(); SaveCodexSettings(); UpdateCodexThread();
            ChatBindingEvidenceText.Text = "Привязка явно принята и сохранена. Проверка кандидата сама по себе её не меняла.";
        }
        finally { loadingReasoning = false; Refresh(); }
    });
    private async void ReplaceChatBinding_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        RequireBindingRecovery(); loadingReasoning = true; Refresh();
        try
        {
            var decision = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Неизвестный исход создания чата",
                Content = "Предыдущий чат мог быть создан. Новое намерение может привести к отдельному чату. Предыдущий неопределённый исход будет сохранён. Создание произойдёт при следующем явном старте или передаче контекста.",
                PrimaryButtonText = "Разрешить новый чат", CloseButtonText = "Оставить нерешённым", DefaultButton = ContentDialogButton.Close };
            if (await decision.ShowAsync() != ContentDialogResult.Primary) return;
            if (bindingDiagnostic is null) await codex.ReplaceUncertainBindingAsync(codexLifetime.Token);
            else { bindingDiagnostic.Binding.ReplaceUncertainCreation(); await ReloadDiagnosticBindingAsync(); }
            bindingReview.Invalidate(); ChatBindingEvidenceText.Text = "Предыдущее неопределённое создание сохранено; новое намерение разрешено явно.";
            SaveCodexSettings(); UpdateCodexThread();
        }
        finally { loadingReasoning = false; Refresh(); }
    });
    private void SaveCodexSettings() => journal?.SaveCodexSettings(new(codex.ThreadId, codex.ReasoningEffort));
    private async Task LoadReasoningAsync()
    {
        loadingReasoning = true;
        try
        {
            var options = await codex.GetReasoningOptionsAsync(CodexDirectory(), codexLifetime.Token);
            string selected = codex.ReasoningEffort ?? "";
            var choices = new[] { new ReasoningOption("", $"По умолчанию в Codex ({codex.DefaultReasoningEffort})") }.Concat(options).ToList();
            // Сохранённый неподдерживаемый выбор не заменяем молча.
            if (!choices.Any(option => option.Value == selected)) choices.Add(new(selected, selected + " · недоступен"));
            ReasoningSelector.ItemsSource = choices;
            ReasoningSelector.SelectedItem = choices.First(option => option.Value == selected);
        }
        finally { loadingReasoning = false; }
    }
    private async void LoadReasoning_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (starting || transferring || loadingReasoning) throw new InvalidOperationException("Дождитесь завершения операции Codex.");
        LoadReasoningButton.IsEnabled = false;
        try { await LoadReasoningAsync(); }
        finally { if (!closing) LoadReasoningButton.IsEnabled = true; }
    });
    private void Reasoning_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loadingReasoning || ReasoningSelector.SelectedItem is not ReasoningOption option) return;
        string? previous = codex.ReasoningEffort;
        codex.ReasoningEffort = option.Value.Length == 0 ? null : option.Value;
        try { SaveCodexSettings(); }
        catch (Exception error)
        {
            codex.ReasoningEffort = previous;
            loadingReasoning = true;
            ReasoningSelector.SelectedItem = ReasoningSelector.Items.Cast<ReasoningOption>().FirstOrDefault(item => item.Value == (previous ?? ""));
            loadingReasoning = false;
            ShowNotice("Уровень рассуждения не сохранён: " + error.Message);
        }
    }
    private async Task StartCaptureAsync()
    {
        if (starting) throw new InvalidOperationException("Старт уже выполняется.");
        if (transferring || loadingReasoning) throw new InvalidOperationException("Дождитесь завершения операции Codex.");
        if (session.Phase is SessionPhase.Capturing or SessionPhase.Paused) return;
        starting = true; Refresh();
        try
        {
            if (CaptureMode.SelectedIndex == 1)
            {
                RefreshSources(true);
                _ = (audioSelection ?? throw new IOException("Каталог источников недоступен.")).RequireTarget();
            }
            await PrepareSessionAsync(); await Configure();
            // Готовность модели не является условием сохранения аудио.
            codexLifetime.Token.ThrowIfCancellationRequested();
            await session.StartAsync();
            if (simulation is null && audioSelection?.Status() is { MicrophoneAvailability: "available", Selected.MicrophoneDeviceId: { } deviceId })
            {
                try { AudioGlobalPreferences.Save(GlobalAudioPreferencesPath(), new(1, deviceId)); }
                catch (Exception error) { ShowNotice("Запись началась, но последний использованный микрофон не удалось сохранить: " + error.Message); }
            }
        }
        finally { starting = false; if (!closing) Refresh(); }
    }
    private async Task ResumeCaptureAsync()
    {
        if (session.Phase == SessionPhase.Paused && simulation is null) RefreshSources(true);
        await session.ResumeAsync();
    }
    private async void Pause_Click(object sender, RoutedEventArgs e) => await RunUiAsync(() => session.Phase == SessionPhase.Paused ? ResumeCaptureAsync() : session.PauseAsync());
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunUiAsync(session.StopAsync);
    private async void Mode_Changed(object sender, SelectionChangedEventArgs e) { if (initialized) await RunUiAsync(() => session.SetModeAsync(ModeSelector.SelectedIndex == 1 ? AnalysisMode.Automatic : AnalysisMode.Manual)); }
    private async void Answer_Click(object sender, RoutedEventArgs e)
    {
        FunctionalRunTrace.Write("answer_click");
        SessionEventLog.Current?.Write("answer_click_boundaries",new{requestedMs=source.CaptureBoundaryMs,
            receivedMs=(source as BufferedTranscriptSource)?.ReceivedThroughMs,recording=audioCapture?.RecordingStatus});
        // Граница фиксируется до любого асинхронного сохранения контекста.
        await RunUiAsync(async () => { await session.AnswerAsync(); });
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await RunUiAsync(session.RetryAsync);
    private async void AbandonFailed_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        await session.AbandonFailedRequestAsync();
        ShowNotice("Запрос отменён. Расшифровка и история шагов сохранены, граница обработки не изменена. Нажмите «Ответить» для нового запроса.");
    });
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserInput.Text)) { ShowNotice("Введите свой вопрос или инструкцию."); return; }
        await RunUiAsync(async () =>
        {
            await Configure();
            if (await session.AnswerAsync(UserInput.Text.Trim())) UserInput.Text = "";
            else ShowNotice("Предыдущий запрос ещё ожидает обработки. Ваш текст остался в поле.");
        });
    }
    private void Inject_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SpeechInput.Text)) return;
        if (source.Status != CaptureStatus.Capturing) { ShowNotice("Сначала запустите симуляцию."); return; }
        simulation?.Inject(SpeechInput.Text.Trim(), ChannelSelector.SelectedIndex == 0 ? AudioChannel.You : AudioChannel.Other,
            MeaningSelector.SelectedIndex == 1 ? SegmentMeaning.Question : SegmentMeaning.Fact);
        SpeechInput.Text = ""; session.Pulse();
    }
    private void ShowNotice(string text) { Notice.Message = text; Notice.IsOpen = true; }

    private async void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selected = await picker.PickMultipleFilesAsync();
            foreach (var file in selected) await context.AddFileAsync(file.Path);
            RefreshContext();
        }
        catch (Exception error) { RefreshContext(); ShowNotice(error.Message); }
    }
    private async void AttachProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var selected = await picker.PickSingleFolderAsync();
            if (selected is null) return;
            ContextStatusText.Text = "Чтение файлов проекта…";
            var result = await Task.Run(() => context.AddProjectAsync(selected.Path));
            RefreshContext();
            ContextStatusText.Text = $"Импортировано: {result.Added}; пропущено: {result.Skipped}. Ничего не отправлено Codex.";
            if (result.Notes.Count > 0) ShowNotice(string.Join("\n", result.Notes));
        }
        catch (Exception error) { RefreshContext(); ShowNotice(error.Message); }
    }
    private async void ClearContext_Click(object sender, RoutedEventArgs e)
    {
        if (transferring || starting || loadingReasoning || session.Snapshot().IsBusy || session.Snapshot().PendingOperations > 0)
        { ShowNotice("Дождитесь завершения операций Codex перед очисткой контекста."); return; }
        CodexThreadBinding? binding = journal is null ? null : new(journal.DirectoryPath);
        try { binding?.Reset(); }
        catch (Exception error) { ShowNotice(error.Message); return; }
        context.Clear(); ContextInput.Text = ""; RefreshContext();
        string? effort = codex.ReasoningEffort;
        await codex.DisposeAsync(); codex = new(new CodexSessionSettings(null, effort), binding);
        SaveCodexSettings(); UpdateCodexThread();
        ContextStatusText.Text = "Локальные вложения очищены. Следующая передача начнёт новую сессию. Уже переданные ходы остаются в истории Codex.";
    }
    private void SearchTranscript_Click(object sender, RoutedEventArgs e)
    {
        string input = TranscriptSearchInput.Text.Trim();
        visibleTranscriptQuery = Guid.TryParse(input, out var id) ? new(SegmentId: id) : new(Text: input);
        ShowTranscriptPage(null);
    }
    private async void ReconcileModel_Click(object sender, RoutedEventArgs e) => await RunUiAsync(async () =>
    {
        if (journal is null) throw new InvalidOperationException("Выберите сохранённую реальную сессию.");
        foreach (var operation in journal.ModelOperations.Where(item => item.Phase is ModelOperationPhase.ReconciliationRequired
            or ModelOperationPhase.RequestSentUnknown or ModelOperationPhase.RemoteAccepted))
            if (retrievalBackend is not null && journal.ReadRetrieval(operation.OperationId) is not null)
                await engine.ReconcileIntermediateAsync(operation.OperationId, (item, token) => retrievalBackend.ReconcileStepAsync(item.OperationId, token));
            else
                await engine.ReconcileAsync(operation.OperationId, async (item, token) => CodexModelBackend.ParseResult(item.Request,
                    await codex.ReadOperationResultAsync(item.OperationId, item.RemoteOperationId, CodexDirectory(), token)));
    });
    private void MoreTranscript_Click(object sender, RoutedEventArgs e) => ShowTranscriptPage(visibleTranscriptCursor);
    private void ClearTranscriptSearch_Click(object sender, RoutedEventArgs e)
    {
        visibleTranscriptQuery = null; visibleTranscriptCursor = null;
        MoreTranscriptButton.IsEnabled = false; TranscriptSearchStatus.Text = "";
        transcriptProjection.Synchronize(source.Segments);
    }
    private void ShowTranscriptPage(string? cursor)
    {
        try
        {
            var page = transcriptQueries.Read(visibleTranscriptQuery ?? new(), 100, cursor);
            transcriptProjection.Synchronize(page.Segments);
            visibleTranscriptCursor = page.NextCursor; MoreTranscriptButton.IsEnabled = page.HasMore;
            TranscriptSearchStatus.Text = $"Найдено окончательных сегментов: {page.Total}. На странице: {page.Segments.Count}. ID и исходный текст доступны для копирования.";
        }
        catch (Exception error) { ShowNotice(error.Message); }
    }
    private void RefreshContext()
    {
        ContextFiles.Clear();
        foreach (var file in context.Files) ContextFiles.Add(new(file.Name, $"{file.Bytes:N0} байт"));
        preparedContext = context.BuildPrompt(); _ = RunUiAsync(Configure);
        AttachmentsText.Text = $"{context.Files.Count} файлов · {context.Files.Sum(f => f.Bytes):N0} байт" +
            (context.ProjectRoot is { } root ? $"\nПроект: {root}" : "");
        ContextStatusText.Text = "Контекст подготовлен. Для передачи рабочей сессии Codex нажмите кнопку ниже.";
    }
    private async void TransferContext_Click(object sender, RoutedEventArgs e)
    {
        if (transferring) return;
        if (starting || loadingReasoning) { ShowNotice("Дождитесь завершения подготовки Codex."); return; }
        string text = ContextInput.Text.Trim() + "\n\n" + preparedContext + "\n\n" + SharedChannelContext;
        if (string.IsNullOrWhiteSpace(text)) { ShowNotice("Сначала задайте контекст или прикрепите файлы."); return; }
        transferring = true; TransferButton.IsEnabled = false;
        Refresh();
        ContextStatusText.Text = "Передача контекста GPT-6.1 Terra через Codex…";
        try
        {
            string cwd = CodexDirectory();
            await codex.EnsureReadyAsync(cwd, codexLifetime.Token);
            UpdateCodexThread(); SaveCodexSettings();
            string response = await codex.SendAsync(text, cwd, codexLifetime.Token);
            if (!closing)
            {
                ContextStatusText.Text = "Контекст передан рабочей сессии Codex. Анализ фиксируется в журнале модельных операций.";
                Chat.Add(new(DateTimeOffset.Now.ToString("HH:mm:ss") + " · GPT-6.1 Terra · приём контекста", response));
                OnNewMessage();
            }
        }
        catch (Exception error) { if (!closing) { ContextStatusText.Text = "Контекст не подтверждён Codex."; ShowNotice(error.Message); } }
        finally { transferring = false; if (!closing) { TransferButton.IsEnabled = true; Refresh(); } }
    }

    private Task<object> DispatchControl(string method, JsonElement parameters)
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (closing || !DispatcherQueue.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await HandleControl(method, parameters)); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new IOException("Приложение закрывается."));
        return completion.Task;
    }
    private async Task<object> HandleControl(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "model_search_transcript":
                Guid operationId = parameters.GetProperty("operationId").GetGuid();
                string archiveText = parameters.TryGetProperty("text", out var archiveTextValue) ? archiveTextValue.GetString() ?? "" : "";
                Guid? archiveSegmentId = parameters.TryGetProperty("segmentId", out var archiveIdValue) && archiveIdValue.ValueKind != JsonValueKind.Null ? archiveIdValue.GetGuid() : null;
                string? archiveCursor = parameters.TryGetProperty("cursor", out var archiveCursorValue) ? archiveCursorValue.GetString() : null;
                int archiveLimit = parameters.TryGetProperty("limit", out var archiveLimitValue) ? archiveLimitValue.GetInt32() : 100;
                return engine.ReadModelOriginals(operationId, new(Text: archiveText, SegmentId: archiveSegmentId), archiveLimit, archiveCursor);
            default: return await controlDispatcher.HandleAsync(method, parameters);
        }
    }
    private object ControlStatus()
    {
        var state = session.Snapshot();
        var captureDuration = session.Duration.Snapshot();
        string actualCapture = audioCapture?.RecordingStatus is { } recording
            ? recording.Accepting ? "Capturing" : session.Phase == SessionPhase.Paused ? "Paused" : "Stopped"
            : !session.RecordingStopped ? "Capturing" : state.Capture.ToString();
        return new { capture = actualCapture, transcriptionCaptureState=state.Capture.ToString(), mode = state.Mode.ToString(), isSimulation = simulation is not null,
            activeCaptureDurationMs = captureDuration.ActiveCaptureDurationMs,
            activeCaptureDurationUncertain = captureDuration.DurationUncertain,
            transcriptMayBeIncomplete = captureDuration.TranscriptMayBeIncomplete,
            captureDuration, captureDurationCheckpointIntervalMs = CaptureDuration.CheckpointIntervalMs,
            phase = session.Phase.ToString(), sessionError = session.Error,
            audioSources = audioSelection?.Status(), sourceConnection = simulation is not null ? "simulation" : session.Phase == SessionPhase.Capturing ? "connected" : session.Phase.ToString(),
            audioTimeline = audioTimeline?.Snapshot(), audioLevels = new { you = audioCapture?.YouPeak, other = audioCapture?.OtherPeak },
            transcriptionMetrics = (source as BufferedTranscriptSource)?.Metrics,
            rollingContextSeconds = engine.RollingContextSeconds,
            sttWorkerMetrics = transcriber?.Metrics,
            receivedThroughMs = source is BufferedTranscriptSource buffer ? buffer.ReceivedThroughMs : state.CaptureBoundaryMs,
            state.CaptureBoundaryMs, state.FinalizedThroughMs, state.ProcessedThroughMs, state.IsBusy, state.Error,
            state.PendingOperations, attachedFileCount = context.Files.Count, audioRetained = audioCapture?.RecordingStatus is not null,
            audioRecording = audioCapture?.RecordingStatus, audioDiagnostics=audioCapture?.DiagnosticStatus,
            sttRecovery=(source as BufferedTranscriptSource)?.RecoverySummary,
            transcriptionCompletion=session.TranscriptionCompletion, recordingStopped=session.RecordingStopped,
            eventLog = SessionEventLog.Current?.FilePath, eventLogError = SessionEventLog.Current?.Error };
    }

    private void Refresh()
    {
        if (closing) return;
        var snapshot = session.Snapshot();
        if (FunctionalRunTrace.Error is { } metricsError) ShowNotice(metricsError);
        var duration = session.Duration.Snapshot();
        CaptureDurationText.Text = "Активный захват: " + CaptureDuration.Format(duration.ActiveCaptureDurationMs) + (simulation is not null ? " · симуляция" : "");
        CaptureRecoveryText.Text = (duration.DurationUncertain ? "Длительность неполна: аварийный интервал или время старой сессии неизвестны. " : "") +
            (duration.TranscriptMayBeIncomplete ? "Незавершённая транскрипция могла быть утрачена; сохранённый окончательный текст доступен полностью." : "");
        OpenFolderButton.IsEnabled = ExportTranscriptButton.IsEnabled = journal is not null;
        bool canRecoverBinding = codex.BindingRecoveryPending && session.Phase == SessionPhase.Stopped
            && !starting && !preparing && !transferring && !loadingReasoning;
        InspectChatBindingButton.IsEnabled = ReplaceChatBindingButton.IsEnabled = canRecoverBinding;
        AcceptChatBindingButton.IsEnabled = canRecoverBinding && bindingReview.Evidence is not null;
        NewSessionButton.IsEnabled = session.Phase == SessionPhase.Stopped && !preparing && !starting && !transferring && !loadingReasoning;
        ReasoningSelector.IsEnabled = !starting && !transferring && !loadingReasoning;
        LoadReasoningButton.IsEnabled = !starting && !transferring && !loadingReasoning;
        StartButton.IsEnabled = session.Phase == SessionPhase.Stopped && !preparing && !starting && !transferring && !loadingReasoning;
        TransferButton.IsEnabled = !starting && !transferring && !loadingReasoning;
        CaptureMode.IsEnabled = MicrophoneSelector.IsEnabled = ProcessSelector.IsEnabled = RefreshSourcesButton.IsEnabled = ModelPathInput.IsEnabled = RestoreSessionButton.IsEnabled = SessionPathInput.IsEnabled = session.Phase == SessionPhase.Stopped && !preparing && !starting && !transferring && !loadingReasoning;
        VadToggle.IsEnabled = VadPathInput.IsEnabled = ModelPathInput.IsEnabled;
        RetainTestAudio.IsEnabled=ModelPathInput.IsEnabled;
        WindowPolicySelector.IsEnabled = ProcessSelector.IsEnabled;
        PauseButton.IsEnabled = session.Phase is SessionPhase.Capturing or SessionPhase.Paused;
        StopButton.IsEnabled = session.Phase is SessionPhase.Capturing or SessionPhase.Paused or SessionPhase.Faulted;
        PauseButton.Content = snapshot.Capture == CaptureStatus.Paused ? "Далее" : "Пауза";
        InjectButton.IsEnabled = simulation is not null && snapshot.Capture == CaptureStatus.Capturing;
        AnswerButton.IsEnabled = snapshot.CaptureBoundaryMs > 0;
        RetryButton.Visibility = snapshot.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        AbandonFailedButton.Visibility = FailedRequestHelp.Visibility = snapshot.CanAbandonFailedRequest ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = (session.Error ?? snapshot.Error) is { } error ? $"Ошибка: {error}" : $"{session.Phase} · {snapshot.Activity} · в очереди: {snapshot.PendingOperations}";
        if (session.TranscriptionCompletion is { State: not TranscriptStopState.Open } completion)
            StatusText.Text += "\n" + SessionStatusPresentation.Completion(completion.State, session.RecordingStopped);
        if(audioCapture?.RecordingStatus is {} recording)
            StatusText.Text += recording.Error is {} diskError ? "\nАУДИО НЕПОЛНО: "+diskError : $"\nАудиофайлы: {(recording.Accepting?"ЗАПИСЬ":"остановлены")} · частей {recording.CompletedParts} · очередь {recording.QueuedSamples} samples";
        if(SessionEventLog.Current?.Error is {} logError)StatusText.Text+="\n"+logError;
        CountdownText.Text = snapshot.NextAnalysisIn is { } remaining ? $"Следующий анализ через {Math.Ceiling(remaining.TotalSeconds):0} с" : "Таймер не запущен";
        long received = source is BufferedTranscriptSource buffer ? buffer.ReceivedThroughMs : snapshot.CaptureBoundaryMs;
        BoundariesText.Text = $"Запрошенная граница: {Position(snapshot.CaptureBoundaryMs)}\nПодтверждено PCM: {Position(received)}\nОкончательный текст: {Position(snapshot.FinalizedThroughMs)}\nУспешно сохранён анализ: {Position(snapshot.ProcessedThroughMs)}\n" +
            (simulation is null ? $"Журнал операций Terra: {journal?.ModelOperations.Count ?? 0}" : $"Вызовов имитатора: {model.CallCount}");
        QuestionsText.Text = snapshot.Conversation.State.Questions.Count == 0 ? "Вопросов пока нет." :
            string.Join("\n\n", snapshot.Conversation.State.Questions.Select(q => $"{QuestionLabel(q.Status)} · {q.Text}"));
        FactsText.Text = string.Join("\n", snapshot.Conversation.State.Facts.Concat(snapshot.Conversation.State.Commitments).Select(f => "• " + f));
        foreach (var message in snapshot.Conversation.Messages)
            if (seenMessages.Add(message.OperationId))
            {
                Chat.Add(new(message.CreatedAt.ToLocalTime().ToString("HH:mm:ss") + (simulation is null ? " · GPT-6.1 Terra" : " · имитатор"), message.UserInstruction is null
                    ? message.Text : $"Ваш запрос: {message.UserInstruction}\n\n{message.Text}"));
                FunctionalRunTrace.Write("answer_added_to_ui_collection", message.OperationId);
                OnNewMessage();
            }
        if (visibleTranscriptQuery is null) transcriptProjection.Synchronize(source.Segments);
        YouLevel.Value = audioCapture?.YouPeak ?? (source.Status == CaptureStatus.Capturing && source.Segments.Any(s => !s.IsFinal && s.Channel == AudioChannel.You) ? 0.65 : 0);
        OtherLevel.Value = audioCapture?.OtherPeak ?? (source.Status == CaptureStatus.Capturing && source.Segments.Any(s => !s.IsFinal && s.Channel == AudioChannel.Other) ? 0.55 : 0);
        if (bindingDiagnostic?.FullShell == true) RestrictShellActions(MainLayout);
    }
    private void OnNewMessage()
    {
        if (followChat)
        {
            chatScrollPending = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                ChatScroll.UpdateLayout();
                ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true);
                chatScrollPending = false;
            });
        }
        else LatestButton.Visibility = Visibility.Visible;
    }
    private void Chat_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && !chatScrollPending) { followChat = ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 20; if (followChat) LatestButton.Visibility = Visibility.Collapsed; }
    }
    private void Latest_Click(object sender, RoutedEventArgs e) { followChat = true; ChatScroll.UpdateLayout(); ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true); LatestButton.Visibility = Visibility.Collapsed; }
    private static string Position(long ms) => TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss\.fff");
    private static string QuestionLabel(QuestionStatus status) => status switch { QuestionStatus.Open => "Открыт", QuestionStatus.AnsweredByParticipant => "Ответил участник", QuestionStatus.Displayed => "Показан помощником", _ => "Неактуален" };
    private async void Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (shutdownAttempted) return;
        if (closing) return;
        args.Cancel = true; closing = true; pulse.Stop(); session.Changed -= Engine_Changed;
        systemUiSettings.ColorValuesChanged -= SystemColorsChanged;
        accessibilitySettings.Changed -= HighContrastChanged;
        codexLifetime.Cancel();
        var errors = new List<string>();
        try { PersistSelectedSttModel(); }
        catch (Exception error) { errors.Add("Выбор STT-модели не сохранён: " + error.Message); }
        async Task Release(Func<Task> action)
        {
            try { await action().WaitAsync(TimeSpan.FromSeconds(75)); }
            catch (Exception error) { errors.Add(error.Message); }
        }
        await Release(() => control.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await Release(() => session.DisposeAsync().AsTask());
        if (session.Error is { } sessionError) errors.Add(sessionError);
        if (transcriber is not null) await Release(() => transcriber.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        if (sessionDiagnostics is not null) await Release(() => sessionDiagnostics.DisposeAsync().AsTask());
        sessionDiagnostics = null;
        await Release(() => codex.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        sessionOwner?.Dispose(); sessionOwner = null;
        sourceCatalog?.Dispose(); sourceCatalog = null;
        shutdownAttempted = true;
        if (errors.Count > 0)
        {
            closing = false;
            ShowNotice("Не всё подтверждено при завершении: " + string.Join("; ", errors) + " Повторное закрытие завершит приложение.");
        }
        else DispatcherQueue.TryEnqueue(Close);
    }
}

public sealed class ChatRow(string header, string text)
{
    public string Header { get; } = header;
    public string Text { get; } = text;
}
public sealed class ContextFileRow(string name, string size)
{
    public string Name { get; } = name;
    public string Size { get; } = size;
}
