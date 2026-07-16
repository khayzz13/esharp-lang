using Esharp.Syntax;

namespace Esharp.Diagnostics;

/// The compiler's pipeline phases, in order. Every diagnostic the internal
/// machinery raises (an ICE, a post-emit metadata defect, an invariant break) is
/// attributed to the phase that raised it, so a failure is traceable to its origin
/// — lex → parse → bind → flow → lower → codegen → emit → verify.
public enum CompilerPhase
{
    Lex,
    Parse,
    Bind,
    FlowAnalysis,
    Lower,
    CodeGen,
    Emit,
    Verify,
}

/// <summary>
/// The compiler's cross-phase observability spine. Two jobs:
///
/// <list type="number">
///   <item><b>ICE containment.</b> <see cref="Guard"/> runs a unit of work inside a
///     phase and converts any unexpected internal exception into a structured
///     <c>ES9500</c> diagnostic naming the phase, the current <see cref="Work"/> item,
///     and the exception — instead of letting an opaque stack trace escape and take
///     down the whole compilation. A compiler that means to "compile anything valid"
///     must first never crash on anything: every internal failure becomes a located,
///     attributable diagnostic.</item>
///   <item><b>Work-item context.</b> <see cref="Work"/> threads a human label for the
///     thing currently being processed (a file, a declaration, a type, a method, a
///     node) so an ICE — or any phase diagnostic — can say *where* it happened, not
///     just *that* it happened.</item>
/// </list>
///
/// It is deliberately allocation-light and side-effecting only through the
/// <see cref="DiagnosticBag"/> handed in, so it can wrap hot loops (per-type,
/// per-method emit) without changing control flow on the success path.
/// </summary>
public static class CompilerTrace
{
    [ThreadStatic] static string? _workItem;

    /// The label of the work item currently being processed on this thread, or null.
    public static string? CurrentWork => _workItem;

    /// Push a work-item label for the duration of the returned scope. Nesting is
    /// supported — the previous label is restored on dispose, so an outer
    /// "type Foo" can wrap inner "method bar" / "node baz" scopes and an ICE names
    /// the innermost one that was active.
    public static WorkScope Work(string label) => new(label);

    public readonly struct WorkScope : IDisposable
    {
        readonly string? _previous;
        internal WorkScope(string label) { _previous = _workItem; _workItem = label; }
        public void Dispose() => _workItem = _previous;
    }

    /// Run <paramref name="body"/> inside <paramref name="phase"/>. An unexpected
    /// exception is captured as an <c>ES9500</c> internal-compiler-error diagnostic
    /// (phase + current work item + exception) rather than propagating. Returns true
    /// on success, false when an ICE was contained.
    public static bool Guard(DiagnosticBag diagnostics, CompilerPhase phase, SourceSpan span, Action body)
    {
        try { body(); return true; }
        catch (Exception ex)
        {
            ReportIce(diagnostics, phase, span, ex);
            return false;
        }
    }

    /// <see cref="Guard(DiagnosticBag, CompilerPhase, SourceSpan, Action)"/> for a
    /// value-producing unit of work. On an ICE the diagnostic is recorded and
    /// <paramref name="fallback"/> is returned so the caller can continue.
    public static T Guard<T>(DiagnosticBag diagnostics, CompilerPhase phase, SourceSpan span, Func<T> body, T fallback)
    {
        try { return body(); }
        catch (Exception ex)
        {
            ReportIce(diagnostics, phase, span, ex);
            return fallback;
        }
    }

    static void ReportIce(DiagnosticBag diagnostics, CompilerPhase phase, SourceSpan span, Exception ex)
    {
        // Keep the first line of the stack (the throw site) — enough to locate the
        // emitter/binder method without dumping the whole trace into the message.
        var frames = (ex.StackTrace ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var firstFrame = Environment.GetEnvironmentVariable("ES_ICE_FULL") == "1"
            ? string.Join("\n", frames.Take(12).Select(f => f.Trim()))
            : frames.FirstOrDefault()?.Trim() ?? "<no stack>";
        diagnostics.Report(span, DiagnosticDescriptors.InternalCompilerError,
            phase.ToString(),
            _workItem ?? "<unknown>",
            ex.GetType().Name,
            ex.Message,
            firstFrame);
    }
}

// The internal-machinery diagnostic surface (ES95xx). These are not language
// rules a user program violates — they are the compiler reporting on *itself*:
// an internal failure, or a defect in the metadata/IL it just emitted that the
// CLR would reject at load/JIT time but ILVerify (an IL-type-safety checker) does
// not catch. Surfacing them at compile time is what lets the compiler converge on
// "compiles anything valid, and tells you precisely when it can't."
public static partial class DiagnosticDescriptors
{
    /// An unexpected exception escaped a compiler phase — a bug in the compiler,
    /// not the program. Carries the phase, the work item in flight, and the throw.
    public static readonly DiagnosticDescriptor InternalCompilerError = Register(new(
        "ES9500",
        "Internal compiler error",
        "Internal compiler error in {0} while processing {1}: {2}: {3}  (at {4}). " +
        "This is a compiler bug — the input may still be valid E#.",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9500"));

    /// The emitter produced a type that declares an interface but does not supply a
    /// loadable implementation for one of its members — the CLR throws
    /// TypeLoadException ("does not have an implementation") at first use. ILVerify
    /// checks IL type-safety, not the interface map, so this slips past it.
    public static readonly DiagnosticDescriptor InterfaceMemberNotImplemented = Register(new(
        "ES9510",
        "Emitted type does not implement a declared interface member",
        "Emitted type '{0}' declares interface '{1}' but provides no loadable implementation " +
        "of member '{2}'. The runtime would reject it with TypeLoadException. " +
        "(For a value type, the implementing method must be virtual + final.)",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9510"));

    /// The emitter formed a generic instantiation whose type argument does not
    /// satisfy the parameter's constraint — the JIT rejects it with a
    /// VerificationException when the call is compiled. Generic-constraint checking
    /// is a metadata/JIT concern ILVerify does not perform.
    public static readonly DiagnosticDescriptor GenericConstraintNotSatisfied = Register(new(
        "ES9511",
        "Emitted generic instantiation violates a constraint",
        "Emitted generic '{0}' is instantiated with type argument '{1}', which does not satisfy " +
        "constraint '{2}'. The runtime would reject it with VerificationException. ",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9511"));

    /// A concrete (non-abstract, non-extern) method was emitted with no method body.
    /// The CLR rejects the type at load — there is nothing to JIT.
    public static readonly DiagnosticDescriptor EmittedMethodHasNoBody = Register(new(
        "ES9512",
        "Emitted concrete method has no body",
        "Emitted method '{0}' is neither abstract nor extern but carries no IL body. " +
        "The runtime would reject the type at load (nothing to execute).",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9512"));

    /// An abstract method was emitted on a type that is neither abstract nor an
    /// interface — the CLR cannot instantiate it and rejects it at load.
    public static readonly DiagnosticDescriptor AbstractMemberOnConcreteType = Register(new(
        "ES9513",
        "Abstract member on a concrete type",
        "Emitted type '{0}' is instantiable (not abstract) yet declares abstract method '{1}'. " +
        "The runtime would reject it at load — an instantiable type must implement all its members.",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9513"));

    /// A MethodImpl override points at a slot that is not on any interface the type
    /// declares nor any base — the override token does not resolve at load.
    public static readonly DiagnosticDescriptor OverrideTargetNotFound = Register(new(
        "ES9514",
        "Method override targets an unknown slot",
        "Emitted method '{0}' overrides '{1}', but that method is on no interface the type " +
        "implements and no base type — the runtime cannot bind the override at load.",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9514"));

    /// A value type contains an instance field of its own type by value — an
    /// infinitely-sized layout the CLR rejects at load (the binder's ES2002 normally
    /// catches this in source; this is the emit-side backstop for synthesized types).
    public static readonly DiagnosticDescriptor ValueTypeLayoutCycle = Register(new(
        "ES9515",
        "Value type has an infinite layout cycle",
        "Emitted value type '{0}' has instance field '{1}' of its own type by value — " +
        "an infinitely-sized layout the runtime rejects at load. Break the cycle with a pointer.",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9515"));

    /// Two members with the same name and identical signature were emitted on one
    /// type — a duplicate definition the metadata writer or the runtime rejects.
    public static readonly DiagnosticDescriptor DuplicateMemberSignature = Register(new(
        "ES9516",
        "Duplicate member signature",
        "Emitted type '{0}' declares member '{1}' more than once with the same signature — " +
        "a duplicate definition the runtime rejects at load.",
        DiagnosticSeverity.Error,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9516"));

    /// A method signature, field, or local resolved to System.Object where the binder
    /// intended a concrete type — the emitter silently erased a type it could not
    /// resolve. Reported as a warning: the IL is loadable, but the erasure is almost
    /// always a latent wrong-overload / boxing / cast bug downstream.
    public static readonly DiagnosticDescriptor TypeErasedToObject = Register(new(
        "ES9517",
        "Type erased to object during emit",
        "While emitting '{0}', type '{1}' could not be resolved and was erased to System.Object. " +
        "The assembly loads, but the erasure is usually a latent mis-compile (wrong overload, " +
        "missing box/cast). Reference the declaring assembly or check the name.",
        DiagnosticSeverity.Warning,
        "Internal",
        "https://esharp-lang.vercel.app/diagnostics/ES9517"));
}
