using System.Text.Json;

namespace SpeechAnalyzer.Core;

public sealed record ExperimentLimits(int Calls, long Tokens, long InFlightAllowance);
public enum ExperimentAdmissionRefusal { CallCap, MissingUsage, TokenCeiling }

public sealed class ExperimentAdmissionException(ExperimentAdmissionRefusal reason) : InvalidOperationException(reason switch
{
    ExperimentAdmissionRefusal.CallCap => "Experiment paused: remote call cap reached.",
    ExperimentAdmissionRefusal.MissingUsage => "Experiment paused: usage for a prior admitted turn is missing or ambiguous.",
    ExperimentAdmissionRefusal.TokenCeiling => "Experiment paused: the token ceiling would leave insufficient in-flight allowance.",
    _ => throw new ArgumentOutOfRangeException(nameof(reason))
})
{
    public ExperimentAdmissionRefusal Reason { get; } = reason;
}
public sealed record ExperimentUsage(int Calls, int ObservedTurns, long ObservedTokens, long ObservedOvershoot)
{
    public string? OvershootReason => ObservedOvershoot > 0
        ? "Usage received after admission exceeded the reserved allowance; app-server does not enforce a token cap."
        : null;
}
public sealed class ExperimentBudget
{
    private readonly string directory;
    private readonly ExperimentLimits limits;
    private readonly object gate = new();

    public ExperimentBudget(string directory, ExperimentLimits limits)
    {
        if (limits.Calls <= 0 || limits.Tokens <= 0 || limits.InFlightAllowance <= 0 ||
            limits.InFlightAllowance > limits.Tokens)
            throw new ArgumentOutOfRangeException(nameof(limits));
        this.directory = Directory.CreateDirectory(directory).FullName;
        this.limits = limits;
        string path = Path.Combine(this.directory, "limits.json");
        if (!File.Exists(path)) WriteNew(path, limits);
        if (JsonSerializer.Deserialize<ExperimentLimits>(File.ReadAllText(path)) != limits)
            throw new InvalidOperationException("The experiment budget has already been frozen.");
    }

    public void Reserve(TokenUsageScope scope)
    {
        lock (gate)
        {
            using var owner = OpenOwner();
            var usage = ReadUsage();
            if (usage.Calls >= limits.Calls)
                throw new ExperimentAdmissionException(ExperimentAdmissionRefusal.CallCap);
            if (usage.ObservedTurns != usage.Calls)
                throw new ExperimentAdmissionException(ExperimentAdmissionRefusal.MissingUsage);
            if (usage.ObservedTokens > limits.Tokens - limits.InFlightAllowance)
                throw new ExperimentAdmissionException(ExperimentAdmissionRefusal.TokenCeiling);
            string receipt = Path.Combine(scope.Directory, "token-usage-v1", scope.StepId.ToString("N") + ".json");
            WriteNew(Path.Combine(directory, scope.StepId.ToString("N") + ".attempt"),
                new Admission(scope.OperationId, scope.StepId, receipt));
        }
    }

    public ExperimentUsage Snapshot()
    {
        lock (gate)
        {
            using var owner = OpenOwner();
            return ReadUsage();
        }
    }

    private FileStream OpenOwner() =>
        new(Path.Combine(directory, "budget.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private ExperimentUsage ReadUsage()
    {
        int calls = 0, observed = 0;
        long tokens = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "*.attempt").Take(limits.Calls))
        {
            calls++;
            var admission = JsonSerializer.Deserialize<Admission>(File.ReadAllText(path))!;
            if (!File.Exists(admission.ReceiptPath)) continue;
            var receipt = JsonSerializer.Deserialize<TurnUsageReceipt>(File.ReadAllText(admission.ReceiptPath));
            if (receipt is not { TurnStatus: "completed", Ambiguous: false, Observed: { } counts } ||
                receipt.OperationId != admission.OperationId || receipt.StepId != admission.StepId) continue;
            observed++;
            tokens = checked(tokens + counts.Total);
        }
        return new(calls, observed, tokens, Math.Max(0, tokens - limits.Tokens));
    }

    private static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, value);
        stream.Flush(true);
    }

    private sealed record Admission(Guid OperationId, Guid StepId, string ReceiptPath);
}
