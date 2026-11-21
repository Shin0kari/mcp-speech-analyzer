using SpeechAnalyzer.Core;

SpeechAnalyzer.Testing.TestRunOutput.Start(args, "regression");

// Explicit executable allowlist: no recorder, WAV, native ASR, real model or IPC tests.
Func<Task>[] tests = [
    ModelOperationTests.ResumePreparedAndResult, ModelOperationTests.UncertainOutcomeNeedsReconciliation,
    ModelOperationTests.DurableTransitionsAndRetry, ModelOperationTests.TransitionFailures,
    ReviewRegressionTests.AcceptedBeforeFinalization, ReviewRegressionTests.AcceptanceFailureAndMissingStt,
    ReviewRegressionTests.EmptySttDuringAcceptance, ReviewRegressionTests.QueuedAcceptanceRecovery,
    ReviewRegressionTests.UncertainBarrier, ReviewRegressionTests.PersistentJournalFailure,
    ReviewRegressionTests.UnlabelledLateSpeech, ReviewRegressionTests.AutomaticCommitRetryAndRecovery,
    OperationJournalV2Tests.PhasesSourcesAndRestart, OperationJournalV2Tests.LegacyAndMixed,
    OperationJournalV2Tests.PartialTailAndDuplicate, OperationJournalV2Tests.CorruptionAndMissingPredecessor,
    OperationJournalV2Tests.WriteFailureAndSize,
    ApplicationRetrievalTests.ExactPassageAndBoundary, ApplicationRetrievalTests.RemoteRecovery,
    ApplicationRetrievalTests.PersistenceRecovery, ApplicationRetrievalTests.CommitRecovery,
    ApplicationRetrievalTests.PhaseAndSourceRecovery, ApplicationRetrievalTests.Limits,
    ApplicationRetrievalTests.InvalidModelData, ApplicationRetrievalTests.AbandonInvalidResponse,
    ApplicationRetrievalTests.AbandonPersistenceAndBarrier,
    SessionCoordinatorTests.TranscriptRows, SessionCoordinatorTests.Transitions, SessionCoordinatorTests.PartialStart,
    SessionCoordinatorTests.StopTail, SessionCoordinatorTests.ClosePending, SessionCoordinatorTests.CloseDuringModel,
    SessionCoordinatorTests.SourceFailure, SessionCoordinatorTests.CloseDuringSave, SessionCoordinatorTests.HungSourceStop,
    InterpretationTests.Validation, InterpretationTests.PersistenceAndRecovery,
    TimerBoundary, BackgroundClick, KnownFailureRetry
];
int failures = 0;
foreach(var test in tests)
{
    string name = test.Method.DeclaringType!.Name + "." + test.Method.Name;
    try { await test().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine("PASS " + name); }
    catch(Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
}
Console.WriteLine($"{tests.Length-failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if(!condition) throw new Exception(message); }
static async Task TimerBoundary()
{
    await using var f = new Fixture(); f.Engine.Start();
    f.Advance(60); await f.Idle(); Check(f.Backend.Requests.Length == 0, "Silence called model");
    f.Add("Initial question", SegmentMeaning.Question, "q"); f.Advance(60); await f.Idle();
    Check(f.Store.Read().Messages.Count == 0 && f.Store.Read().State.ProcessedThroughMs == 120001, "Background timer failed");
    f.Advance(20); f.Add("Remainder"); long cut = f.Source.CaptureBoundaryMs;
    f.Engine.Answer(); f.Add("Future"); await f.Idle();
    Check(f.Backend.Requests.Last().BoundaryMs == cut && f.Backend.Requests.Last().Segments.Single().Text == "Remainder", "Click evidence contaminated");
    Check(f.Engine.Snapshot().NextAnalysisIn == TimeSpan.FromSeconds(60), "Click did not reset timer");
    f.Advance(59); await f.Idle(); Check(f.Backend.Requests.Length == 2, "Timer fired early");
    f.Advance(1); await f.Idle(); Check(f.Backend.Requests.Length == 3 && f.Backend.Requests.Last().Segments.Single().Text == "Future", "Timer lost future");
}
static async Task BackgroundClick()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Background");
    f.Backend.HoldNext = new(TaskCreationOptions.RunContinuationsAsynchronously); var release = f.Backend.HoldNext;
    f.Advance(60); await f.Backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    f.Advance(20); f.Add("Click remainder"); var cut = f.Source.CaptureBoundaryMs;
    f.Engine.Answer(); f.Add("Future"); release.SetResult(); await f.Idle();
    Check(f.Backend.MaxConcurrent == 1 && f.Backend.Requests.Length == 2, "Model state changes overlapped");
    Check(f.Backend.Requests.Last().Previous.ProcessedThroughMs == f.Backend.Requests.First().BoundaryMs && f.Backend.Requests.Last().BoundaryMs == cut,
        "Click did not reuse successful background");
    Check(f.Backend.Requests.Last().Segments.Single().Text == "Click remainder", "Wrong remaining evidence");
}
static async Task KnownFailureRetry()
{
    await using var f = new Fixture(); f.Engine.Start(); f.Add("Keep original"); f.Backend.FailNext = true;
    f.Engine.Answer(); await f.Idle(); Check(f.Engine.Snapshot().CanRetry && f.Store.Read().State.ProcessedThroughMs == 0, "Transport advanced");
    f.Engine.Retry(); await f.Idle();
    Check(f.Backend.Requests.Length == 2 && f.Backend.Requests[0].OperationId == f.Backend.Requests[1].OperationId &&
        f.Backend.Requests[1].Segments.Single().Text == "Keep original" && f.Store.Read().Messages.Count == 1, "Known failure retry lost evidence");
}
