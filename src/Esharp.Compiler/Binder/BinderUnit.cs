using Esharp.Diagnostics;
using Esharp.Syntax;

namespace Esharp.Binder;

/// Base for the binder units (expressions, statements, match, declarations) —
/// the binding-side `ParserUnit`. Each unit is a real class with a single
/// responsibility; they share one `BindContext` and reach each other through the
/// root `Binder`. The thin accessors here keep the binding methods reading the
/// same as before the carve, while the mutable state lives in exactly one place.
internal abstract class BinderUnit
{
    protected readonly Binder B;

    protected BinderUnit(Binder binder) => B = binder;

    protected BindContext Ctx => B.Ctx;
    protected CompilationData Data => B.Ctx.Data;
    protected DiagnosticBag Diagnostics => B.Ctx.Data.Diagnostics;
    protected BinderScope Scope { get => B.Ctx.Scope; set => B.Ctx.Scope = value; }

    /// Declare a local (let/var, parameter, match binding) AND report it to the
    /// semantic sink — one interned LocalSymbol the scope hands back at every use,
    /// so declaration and use are reference-identical occurrences. The enclosing
    /// block span comes from the function being bound (the scope range
    /// LookupSymbolsInScope tests against). Batch binding's null sink makes this a
    /// no-op past the scope write. Returns the interned symbol so the producing
    /// declaration node can carry it (`BoundVariableDeclaration.Local`) — CodeGen
    /// then reads slot identity from the spine instead of re-deriving it from Name.
    protected Esharp.Symbols.LocalSymbol DeclareLocal(string name, BoundType type, bool mutable, SourceSpan span,
        bool isParameter, LocalRepresentation representation = LocalRepresentation.Default)
    {
        var symbol = new Esharp.Symbols.LocalSymbol
        {
            Name = name,
            DeclaringFunction = Ctx.CurrentFunctionName,
            Mutable = mutable,
            Representation = representation,
            IsParameter = isParameter,
            Type = type,
            Span = span,
            BlockSpan = Ctx.CurrentFunctionBlockSpan,
        };
        Scope.DeclareLocal(symbol, type, mutable);
        Data.Sink.OnLocalResolved(symbol, span, Semantics.SymbolOccurrence.Declaration);
        return symbol;
    }

    // Cross-domain wiring — the high-frequency siblings.
    protected ExpressionBinder Expressions => B.Expressions;
    protected StatementBinder Statements => B.Statements;
    protected MatchBinder Match => B.Match;
    protected DeclarationBinder Declarations => B.Declarations;
    protected TypeResolver Types => B.Types;

    // Type-resolution conveniences (the former Binder.Types.cs delegation shims) —
    // pure delegation so the binding methods keep their original shape.
    protected BoundType ResolveType(TypeSyntax syntax) => B.Types.ResolveType(syntax);
    protected BoundType ResolveTypeName(string name) => B.Types.ResolveTypeName(name);
    protected BoundType ResolveGenericArg(TypeSyntax arg) => B.Types.ResolveGenericArg(arg);
    protected BoundType ResolveHeapPointerAware(TypeSyntax syntax) => B.Types.ResolveHeapPointerAware(syntax);
    protected BoundType ResolveMemberType(BoundType target, string member) => B.Types.ResolveMemberType(target, member);
    protected DataClassification ClassifyData(string name) => B.Types.ClassifyData(name);
    protected Esharp.Symbols.TypeSymbol? SymbolOf(DataType dt) => B.Types.SymbolOf(dt);
    protected IReadOnlyList<Esharp.Symbols.FieldSymbol>? FieldsOf(DataType dt) => B.Types.SymbolOf(dt)?.Fields;
    protected Type? ResolveExternalToRuntime(ExternalType ext) => B.Types.ResolveExternalToRuntime(ext);
    protected Type? ResolveExternalRuntimeTypeByName(string name) => B.Types.ResolveExternalRuntimeTypeByName(name);
    protected Type? FindOpenGenericByName(string baseName, int arity) => B.Types.FindOpenGenericByName(baseName, arity);
    protected Type? ResolveBoundTypeToRuntime(BoundType t) => B.Types.ResolveBoundTypeToRuntime(t);
    protected bool IsNamespaceInScope(string ns) => B.Types.IsNamespaceInScope(ns);
    protected bool IsByRefLike(BoundType type) => ResolveBoundTypeToRuntime(type)?.IsByRefLike == true;

    protected static BoundType MapRuntimeTypeToBoundType(Type t) => TypeResolver.MapRuntimeTypeToBoundType(t);
    protected static BoundType MapRuntimeWithSubstitution(Type t, IReadOnlyList<BoundType> args) => TypeResolver.MapRuntimeWithSubstitution(t, args);
    protected static BoundType MapRuntimeTypeWithBoundArgs(Type t, Dictionary<string, BoundType> map) => TypeResolver.MapRuntimeTypeWithBoundArgs(t, map);
    protected static bool LooksLikeTypeName(string name) => TypeResolver.LooksLikeTypeName(name);
    protected static string TypeDisplayName(BoundType t) => TypeResolver.TypeDisplayName(t);
    protected static bool PointerInnerMatches(BoundType a, BoundType b) => TypeResolver.PointerInnerMatches(a, b);
    protected static (bool ByRef, bool ReadOnlyByRef) RefFlags(TypeSyntax syntax) => TypeResolver.RefFlags(syntax);
    protected static string TypeSyntaxLeafName(TypeSyntax t) => TypeResolver.TypeSyntaxLeafName(t);
    protected static string InterfaceName(TypeSyntax t) => TypeResolver.InterfaceName(t);

    // === Flow narrowing (§type-narrowing-and-downcasting) ===
    protected NarrowingAnalyzer Narrowing => B.Narrowing;

    /// A snapshot of the active narrows + poison hints, for save/restore around a
    /// guarded region (a branch, a `&&` right operand, a block).
    protected readonly record struct NarrowSnapshot(
        Dictionary<string, NarrowFact> Narrowed,
        Dictionary<string, BoundType> Poisoned);

    protected NarrowSnapshot SaveNarrows() =>
        new(new(Ctx.Narrowed, StringComparer.Ordinal), new(Ctx.PoisonedNarrows, StringComparer.Ordinal));

    protected void RestoreNarrows(NarrowSnapshot s)
    {
        Ctx.Narrowed = s.Narrowed;
        Ctx.PoisonedNarrows = s.Poisoned;
    }

    /// Apply extracted narrowings to the current region: a stable one becomes an active
    /// smart-cast; an unstable one (a `var`, a call-crossed field) a poison hint.
    protected void ApplyNarrows(IEnumerable<NarrowingAnalyzer.Narrow> facts)
    {
        foreach (var f in facts)
            if (f.Stable)
                Ctx.Narrowed[f.Path] = new NarrowFact(f.Type, f.CallSensitive, Ctx.CallGeneration);
            else
                Ctx.PoisonedNarrows[f.Path] = f.Type;
    }
}
