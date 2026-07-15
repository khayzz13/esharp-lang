using System.Text.Json;
using System.Text.Json.Serialization;

namespace Esharp.FuzzTests.Execution;

/// Where in the pipeline a case ended. `Invoke` covers JIT + runtime of the
/// generated program; everything before it is the compiler's own execution.
public enum FuzzStage
{
    Parse,
    Bind,
    Emit,
    Verify,
    Load,
    Invoke,
}

/// The mechanical outcome taxonomy. Every oracle is phrased over these — the
/// critical separations are Ice (compiler must never throw), VerifierError
/// (binder accepted, ILVerify rejected), and JitReject (verifier accepted,
/// the runtime rejected) — three distinct bug classes that a single
/// catch(Exception) conflates.
public enum OutcomeKind
{
    /// Compiled, verified, loaded, ran; ValueText holds the rendered result.
    Success,

    /// The compiler rejected the input with error diagnostics — the correct
    /// behavior for invalid input, a finding for valid-by-construction input.
    Rejected,

    /// ILVerify failed on emitted IL (ES0900): the binder accepted a program
    /// and the backend produced unverifiable IL. Always a compiler bug.
    VerifierError,

    /// Internal compiler error: an unhandled exception inside lex/parse/bind/
    /// emit. Always a compiler bug, on any input.
    Ice,

    /// The CLR rejected the assembly at load/JIT time (InvalidProgramException,
    /// TypeLoadException, BadImageFormatException...). IL passed the verifier
    /// but the runtime disagrees — always a compiler bug.
    JitReject,

    /// The generated program itself threw an ordinary exception while running.
    RuntimeException,

    /// The per-case watchdog fired — compiler hang or generated-program hang.
    Timeout,

    /// The child process died without reporting (stack overflow, OOM, runtime
    /// assert). Stack overflow in the recursive-descent parser lands here.
    ProcessCrash,

    /// Harness failure (entry point missing, protocol error) — not a compiler
    /// finding; fix the harness.
    Infrastructure,
}

public sealed record SourceFile(string FileName, string Source);

public sealed record CaseRequest(
    string Id,
    IReadOnlyList<SourceFile> Files,
    string EntryType = "Test.Test",
    string EntryMethod = "go",
    bool Invoke = true,
    bool ShortenOpcodes = true,
    bool EmitTwice = false,
    int TimeoutMs = 15_000)
{
    public string PrimarySource => Files[0].Source;
}

public sealed record DiagnosticInfo(string Severity, string Code, string Message, string? File = null, int Line = 0, int Column = 0)
{
    public override string ToString() => $"{Severity} {Code} {File}:{Line}:{Column}: {Message}";
}

public sealed record CaseResult(
    string Id,
    OutcomeKind Kind,
    FuzzStage Stage,
    IReadOnlyList<DiagnosticInfo> Diagnostics,
    string? ValueType = null,
    string? ValueText = null,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    string? TopCompilerFrame = null,
    string? StackTrace = null,
    string? AssemblyHash = null,
    string? SecondAssemblyHash = null,
    long DurationMs = 0)
{
    public bool HasErrorDiagnostics => Diagnostics.Any(d => d.Severity == "Error");

    /// Stable diagnostic fingerprint: codes + severities in order. Message text is
    /// excluded so casts of the same diagnostic with different identifiers bucket together.
    public string DiagnosticSignature => string.Join("|", Diagnostics.Select(d => $"{d.Severity}:{d.Code}"));

    /// The dedupe key: cases sharing a key are the same underlying bug. ICEs key on
    /// stage + exception type + the topmost Esharp.* frame; JIT rejects on exception
    /// type; verifier errors on the diagnostic signature.
    public string BucketKey => Kind switch
    {
        OutcomeKind.Ice => $"ICE/{Stage}/{ExceptionType}/{TopCompilerFrame}",
        OutcomeKind.VerifierError => $"VERIFY/{VerifierFailureKinds()}",
        OutcomeKind.JitReject => $"JIT/{ExceptionType}",
        OutcomeKind.Timeout => $"TIMEOUT/{Stage}",
        OutcomeKind.ProcessCrash => $"CRASH/{ExceptionMessage}",
        OutcomeKind.RuntimeException => $"RUNTIME/{ExceptionType}",
        OutcomeKind.Rejected => $"REJECT/{DiagnosticSignature}",
        OutcomeKind.Infrastructure => $"INFRA/{ExceptionType}",
        _ => "SUCCESS",
    };

    /// The ILVerify failure kinds (PathStackDepth, StackUnexpected, …) pulled
    /// from the ES0900 messages — distinct kinds are distinct bugs, so the
    /// bucket (and the shrinker chasing it) must distinguish them. Message
    /// shape: "… failed verification at <method>: <Kind> — <detail>".
    string VerifierFailureKinds()
    {
        var kinds = Diagnostics
            .Where(d => d.Code == "ES0900")
            .Select(d =>
            {
                var dash = d.Message.IndexOf(" — ", StringComparison.Ordinal);
                if (dash < 0) return "unknown";
                var head = d.Message[..dash];
                var colon = head.LastIndexOf(": ", StringComparison.Ordinal);
                return colon < 0 ? "unknown" : head[(colon + 2)..];
            })
            .Distinct()
            .Order(StringComparer.Ordinal);
        return string.Join("+", kinds);
    }

    public string Describe()
    {
        var lines = new List<string> { $"outcome={Kind} stage={Stage} bucket={BucketKey}" };
        if (ValueText is not null) lines.Add($"value=({ValueType}) {ValueText}");
        if (ExceptionType is not null) lines.Add($"exception={ExceptionType}: {ExceptionMessage}");
        if (Diagnostics.Count > 0) lines.Add("diagnostics:\n  " + string.Join("\n  ", Diagnostics));
        if (StackTrace is not null) lines.Add($"stack:\n{StackTrace}");
        return string.Join("\n", lines);
    }
}

public static class Protocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string ResultPrefix = "RES ";
    public const string ReadyLine = "READY";

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new InvalidOperationException($"Protocol: null after deserializing {typeof(T).Name}.");
}
