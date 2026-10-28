namespace SpeechAnalyzer.Core;

public sealed class SimulatedTranscriptSource(IClock clock) : ITranscriptSource
{
    private readonly object gate = new();
    private readonly List<(TranscriptSegment Segment, TimeSpan ReadyAt)> entries = [];
    private readonly List<(long Boundary, TaskCompletionSource Completion)> waiters = [];
    private CaptureStatus status;
    private long boundary;
    private long sealedBoundary;
    private TimeSpan lastPulse = clock.Now;
    private long nextDemoMs = 6000;
    private int demoIndex;
    public TimeSpan FinalizationDelay { get; set; } = TimeSpan.FromSeconds(1.5);
    public bool DemoSpeechEnabled { get; set; } = true;
    public CaptureStatus Status { get { lock (gate) return status; } }
    public long CaptureBoundaryMs { get { lock (gate) return boundary; } }
    public long FinalizedThroughMs
    {
        get { lock (gate) return entries.Where(e => !e.Segment.IsFinal).Select(e => e.Segment.StartMs).DefaultIfEmpty(boundary).Min(); }
    }
    public IReadOnlyList<TranscriptSegment> Segments
    {
        get { lock (gate) return entries.Select(e => e.Segment).OrderBy(s => s.StartMs).ThenBy(s => s.Channel).ToArray(); }
    }
    public void Start()
    {
        lock (gate) { if (status == CaptureStatus.Stopped) { lastPulse = clock.Now; status = CaptureStatus.Capturing; } }
    }
    public void Pause() { lock (gate) { AdvanceInternal(); if (status == CaptureStatus.Capturing) status = CaptureStatus.Paused; } }
    public void Resume() { lock (gate) { lastPulse = clock.Now; if (status == CaptureStatus.Paused) status = CaptureStatus.Capturing; } }
    public void Stop() { lock (gate) { AdvanceInternal(); status = CaptureStatus.Stopped; } }
    public void Advance() { lock (gate) AdvanceInternal(); }
    public long SnapshotBoundary() { lock (gate) { AdvanceInternal(); sealedBoundary = boundary; return boundary; } }

    public void Inject(string text, AudioChannel channel, SegmentMeaning meaning = SegmentMeaning.Fact,
        string? questionId = null, TimeSpan? delay = null)
    {
        lock (gate)
        {
            if (status != CaptureStatus.Capturing) throw new InvalidOperationException("Сначала запустите симуляцию.");
            AdvanceInternal();
            boundary++; // Сохраняем строгий порядок для нескольких реплик в одну миллисекунду.
            Add(text, channel, Math.Max(sealedBoundary, boundary - 1), boundary, clock.UtcNow,
                clock.Now + (delay ?? FinalizationDelay), meaning, questionId);
            AdvanceInternal();
        }
    }

    public Task WaitForFinalizationAsync(long boundaryMs, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            AdvanceInternal();
            if (boundaryMs > boundary) throw new ArgumentOutOfRangeException(nameof(boundaryMs));
            if (IsReady(boundaryMs)) return Task.CompletedTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((boundaryMs, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private bool IsReady(long cut) => !entries.Any(e => !e.Segment.IsFinal && e.Segment.StartMs < cut);
    private void Add(string text, AudioChannel channel, long start, long end, DateTimeOffset at,
        TimeSpan readyAt, SegmentMeaning meaning, string? questionId)
    {
        entries.Add((new(Guid.NewGuid(), channel, start, end, at, text, false, meaning, questionId), readyAt));
    }

    private void AdvanceInternal()
    {
        var now = clock.Now;
        if (status == CaptureStatus.Capturing)
        {
            boundary += Math.Max(0, (long)(now - lastPulse).TotalMilliseconds);
            while (nextDemoMs <= boundary)
            {
                if (DemoSpeechEnabled && demoIndex < Demo.Length)
                {
                    var line = Demo[demoIndex++];
                    var offset = TimeSpan.FromMilliseconds(boundary - nextDemoMs);
                    // Синтетическое событие занимает 1 мс: не приписываем новый текст
                    // интервалу, который источник уже объявил окончательно готовым.
                    Add(line.Text, line.Channel, Math.Max(sealedBoundary, nextDemoMs - 1), nextDemoMs,
                        clock.UtcNow - offset, now - offset + FinalizationDelay, line.Meaning, line.Id);
                }
                nextDemoMs += 6000;
            }
        }
        lastPulse = now;
        for (int i = 0; i < entries.Count; i++)
            if (!entries[i].Segment.IsFinal && now >= entries[i].ReadyAt)
                entries[i] = (entries[i].Segment with { IsFinal = true }, entries[i].ReadyAt);
        for (int i = waiters.Count - 1; i >= 0; i--)
            if (IsReady(waiters[i].Boundary)) { waiters[i].Completion.TrySetResult(); waiters.RemoveAt(i); }
    }

    // Семантика размечена заранее: это проверка протокола состояния, а не NLP.
    private static readonly (string Text, AudioChannel Channel, SegmentMeaning Meaning, string? Id)[] Demo =
    [
        ("Обсуждаем запуск пилотного проекта для отдела продаж.", AudioChannel.You, SegmentMeaning.Fact, null),
        ("Хотелось бы понять, когда можно начать пилот.", AudioChannel.Other, SegmentMeaning.Question, "pilot-date"),
        ("С нашей стороны можем начать 15 ноября.", AudioChannel.You, SegmentMeaning.ParticipantAnswer, "pilot-date"),
        ("А как будет устроен доступ к отчётам?", AudioChannel.Other, SegmentMeaning.Question, "reports-access"),
        ("Договорились подготовить список пользователей до пятницы.", AudioChannel.You, SegmentMeaning.Commitment, null),
        ("Ещё нужно оценить порядок согласования бюджета.", AudioChannel.Other, SegmentMeaning.Question, "budget-approval")
    ];
}

public sealed class SimulatedModelBackend : IModelBackend
{
    private int failNext;
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);
    public bool FailNext { get => Volatile.Read(ref failNext) != 0; set => Interlocked.Exchange(ref failNext, value ? 1 : 0); }
    public int CallCount => Volatile.Read(ref calls);
    private int calls;

    public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref failNext, 0) == 1) throw new IOException("Имитирован сбой модели. Нажмите «Повторить».");
        return Evaluate(request);
    }

    public static ProcessingResult Evaluate(ProcessingRequest request)
    {
        var facts = request.Previous.Facts.ToList();
        var commitments = request.Previous.Commitments.ToList();
        var questions = request.Previous.Questions.ToDictionary(q => q.Id);
        foreach (var segment in request.Segments)
        {
            var id = segment.QuestionId ?? segment.Id.ToString();
            switch (segment.Meaning)
            {
                case SegmentMeaning.Question:
                    questions.TryAdd(id, new(id, segment.Text, QuestionStatus.Open));
                    break;
                case SegmentMeaning.ParticipantAnswer:
                    if (questions.TryGetValue(id, out var answered)) questions[id] = answered with { Status = QuestionStatus.AnsweredByParticipant };
                    facts.Add(segment.Text);
                    break;
                case SegmentMeaning.ObsoleteQuestion:
                    if (questions.TryGetValue(id, out var obsolete)) questions[id] = obsolete with { Status = QuestionStatus.Obsolete };
                    break;
                case SegmentMeaning.Commitment: commitments.Add(segment.Text); break;
                default: facts.Add(segment.Text); break;
            }
        }
        var open = questions.Values.Where(q => q.Status == QuestionStatus.Open).ToArray();
        string? display = null;
        if (request.Kind != OperationKind.Background && (open.Length > 0 || request.Kind == OperationKind.Answer))
        {
            if (request.UserInstruction.Length > 0)
                display = $"СИМУЛЯЦИЯ · Пользовательский запрос\n{request.UserInstruction}\n\nЗапрос принят с контекстом из {facts.Count} фактов. Реальный ответ появится после подключения Codex.";
            else if (open.Length > 0)
                display = "СИМУЛЯЦИЯ · Вопросы для ответа\n" + string.Join("\n", open.Select(q => $"• {q.Text}")) +
                    "\n\nИмитатор отметил вопросы как показанные. Содержательные ответы будет формировать Codex на следующем этапе.";
            else
                display = $"СИМУЛЯЦИЯ · Резервная инструкция\n{request.FallbackInstruction}\n\nАктуальных неотвеченных вопросов нет. Учтено фактов: {facts.Count}, договорённостей: {commitments.Count}.";
            // При произвольном пользовательском запросе нельзя считать все открытые вопросы закрытыми.
            if (request.UserInstruction.Length == 0)
                foreach (var question in open) questions[question.Id] = question with { Status = QuestionStatus.Displayed };
        }
        return new(new(request.BoundaryMs, facts.ToArray(), commitments.ToArray(), questions.Values.ToArray()), display);
    }
}
