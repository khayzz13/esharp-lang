using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using ILVerify;

namespace Esharp.CodeGen;

/// One verification finding for an emitted method: the declaring `Type::Method`,
/// the ILVerify error code, and ILVerify's human message (which includes the IL
/// offset when token-in-message is enabled). `IsFatal` marks the structural
/// "this method will not run" class (bad stack, fall-through, bad branches) —
/// the family that surfaces as `InvalidProgramException` at invoke time — as
/// opposed to the merely type-unsafe-but-runnable class that E#'s pointer / calli
/// / ldftn features legitimately produce.
public sealed record IlVerificationFinding(string Method, VerifierError Code, string Message)
{
    public bool IsFatal => FatalCodes.Contains(Code);

    /// String form of <see cref="Code"/> so consumers need not reference the
    /// ILVerify package to inspect findings.
    public string CodeName => Code.ToString();

    /// Errors that mean the JIT will reject the method (→ InvalidProgramException),
    /// as opposed to "unverifiable but runnable" type-safety relaxations that E#'s
    /// unsafe surface (managed pointers, `calli`, `ldftn`) deliberately emits.
    static readonly HashSet<VerifierError> FatalCodes =
    [
        VerifierError.UnknownOpcode,
        VerifierError.MethodFallthrough,
        VerifierError.FallthroughException,
        VerifierError.FallthroughIntoHandler,
        VerifierError.FallthroughIntoFilter,
        VerifierError.BadJumpTarget,
        VerifierError.PathStackUnexpected,
        VerifierError.PathStackDepth,
        VerifierError.StackUnexpected,
        VerifierError.StackUnexpectedArrayType,
        VerifierError.StackOverflow,
        VerifierError.StackUnderflow,
        VerifierError.UninitStack,
        VerifierError.UnrecognizedLocalNumber,
        VerifierError.UnrecognizedArgumentNumber,
        VerifierError.ExpectedIntegerType,
        VerifierError.ExpectedFloatType,
        VerifierError.ExpectedNumericType,
        VerifierError.ExpectedTypeToken,
        VerifierError.ExpectedMethodToken,
        VerifierError.ExpectedFieldToken,
        VerifierError.TokenResolve,
        VerifierError.BranchIntoTry,
        VerifierError.BranchIntoHandler,
        VerifierError.BranchIntoFilter,
        VerifierError.BranchOutOfTry,
        VerifierError.BranchOutOfHandler,
        VerifierError.BranchOutOfFilter,
        VerifierError.BranchOutOfFinally,
        VerifierError.ReturnFromTry,
        VerifierError.ReturnFromHandler,
        VerifierError.ReturnFromFilter,
        VerifierError.LeaveIntoTry,
        VerifierError.LeaveIntoHandler,
        VerifierError.LeaveIntoFilter,
        VerifierError.LeaveOutOfFilter,
        VerifierError.Endfinally,
        VerifierError.Endfilter,
    ];
}

/// In-process IL verification — a first-class compiler capability, not a test
/// helper. Built on the `Microsoft.ILVerification` library (the same engine
/// behind `dotnet ilverify`), it runs against a written PE and reports per-method
/// findings. The emit pipeline (see `ILEmitter.EmitToFile(..., verify: true)`),
/// the CLI, and the test harness all consume this one API.
public static class IlVerification
{
    // Microsoft.ILVerification carries process-wide resolver state internally. Separate
    // Verifier instances still cross-contaminate when enumerated concurrently, causing a
    // finding from assembly A to be reported against assembly B. Keep compilation and PE
    // emission parallel, but make this diagnostic boundary single-file-at-a-time.
    static readonly object VerificationGate = new();

    /// Verify every method in the assembly at <paramref name="assemblyPath"/>.
    /// References are resolved from the assembly's own directory, the host app
    /// base directory (where Esharp.Stdlib.dll sits), and the shared framework
    /// directory. Returns one finding per verification error (empty == clean).
    public static IReadOnlyList<IlVerificationFinding> Verify(string assemblyPath, IEnumerable<string>? extraSearchDirs = null)
    {
        lock (VerificationGate)
            return VerifyCore(assemblyPath, extraSearchDirs);
    }

    static IReadOnlyList<IlVerificationFinding> VerifyCore(string assemblyPath, IEnumerable<string>? extraSearchDirs)
    {
        var dirs = new List<string>();
        var asmDir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        if (asmDir is not null) dirs.Add(asmDir);
        if (extraSearchDirs is not null) dirs.AddRange(extraSearchDirs);
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(RuntimeEnvironment.GetRuntimeDirectory());

        using var resolver = new SearchPathResolver(dirs);
        var verifier = new Verifier(resolver, new VerifierOptions { IncludeMetadataTokensInErrorMessages = true });
        verifier.SetSystemModuleName(new AssemblyNameInfo("System.Private.CoreLib"));

        var bytes = File.ReadAllBytes(assemblyPath);
        using var peReader = new PEReader(ImmutableArray.Create(bytes));
        var md = peReader.GetMetadataReader();

        var findings = new List<IlVerificationFinding>();
        foreach (var r in verifier.Verify(peReader))
        {
            var method = "<module>";
            if (!r.Method.IsNil)
            {
                var mdef = md.GetMethodDefinition(r.Method);
                var name = md.GetString(mdef.Name);
                var tdef = md.GetTypeDefinition(mdef.GetDeclaringType());
                var typeName = md.GetString(tdef.Name);
                var nsName = md.GetString(tdef.Namespace);
                method = string.IsNullOrEmpty(nsName) ? $"{typeName}::{name}" : $"{nsName}.{typeName}::{name}";
            }
            var detail = r.Message ?? r.Code.ToString();
            if (r.ErrorArguments is { Length: > 0 })
                detail += " [" + string.Join(", ", r.ErrorArguments.Select(a => $"{a.Name}={a.Value}")) + "]";
            findings.Add(new IlVerificationFinding(method, r.Code, detail));
        }
        return findings;
    }

    /// Only the fatal (won't-JIT) subset — the findings that map to
    /// `InvalidProgramException`. The emit pipeline turns these into diagnostics.
    public static IReadOnlyList<IlVerificationFinding> VerifyFatal(string assemblyPath, IEnumerable<string>? extraSearchDirs = null)
        => Verify(assemblyPath, extraSearchDirs).Where(f => f.IsFatal).ToList();

    /// Resolves assembly/module references by probing a fixed set of directories
    /// for `<name>.dll`. PEReaders are cached and kept open for the lifetime of a
    /// verification run (ILVerify reads them lazily during enumeration).
    sealed class SearchPathResolver(IReadOnlyList<string> dirs) : IResolver, IDisposable
    {
        readonly Dictionary<string, PEReader?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public PEReader? ResolveAssembly(AssemblyNameInfo assemblyName) => Find(assemblyName.Name);

        public PEReader? ResolveModule(AssemblyNameInfo referencingAssembly, string fileName)
            => Find(Path.GetFileNameWithoutExtension(fileName));

        PEReader? Find(string? name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_cache.TryGetValue(name, out var cached)) return cached;
            // Prefer an IMPLEMENTATION assembly over a reference-pack one. A ref-pack
            // core assembly (MSBuild's @(ReferencePath) hands those in) DEFINES
            // System.ValueType / System.Enum instead of forwarding to
            // System.Private.CoreLib, so a struct whose base-type chain resolves
            // through it loses value-type identity to the verifier — every call on
            // it then misreports as "Expected=ref 'T', Found=address of 'T'" against
            // perfectly verifiable IL. The ref assembly is only the last resort when
            // no implementation copy is on any search path.
            PEReader? refOnly = null;
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, name + ".dll");
                if (!File.Exists(candidate)) continue;
                var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(candidate)));
                if (IsReferenceAssembly(pe))
                {
                    if (refOnly is null) refOnly = pe;
                    else pe.Dispose();
                    continue;
                }
                refOnly?.Dispose();
                _cache[name] = pe;
                return pe;
            }
            _cache[name] = refOnly;
            return refOnly;
        }

        static bool IsReferenceAssembly(PEReader pe)
        {
            var md = pe.GetMetadataReader();
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(handle);
                StringHandle nameHandle = default, nsHandle = default;
                if (attr.Constructor.Kind == HandleKind.MemberReference)
                {
                    var mr = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                    if (mr.Parent.Kind != HandleKind.TypeReference) continue;
                    var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    nameHandle = tr.Name;
                    nsHandle = tr.Namespace;
                }
                else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
                {
                    var ctor = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                    var td = md.GetTypeDefinition(ctor.GetDeclaringType());
                    nameHandle = td.Name;
                    nsHandle = td.Namespace;
                }
                if (!nameHandle.IsNil
                    && md.StringComparer.Equals(nameHandle, "ReferenceAssemblyAttribute")
                    && md.StringComparer.Equals(nsHandle, "System.Runtime.CompilerServices"))
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            foreach (var pe in _cache.Values) pe?.Dispose();
            _cache.Clear();
        }
    }
}
