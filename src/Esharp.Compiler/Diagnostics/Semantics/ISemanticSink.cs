using Esharp.Symbols;
using Esharp.Syntax;

namespace Esharp.Diagnostics.Semantics;

public enum SymbolOccurrence { Declaration, Use }

/// The binder's tooling tap — F#'s `TcResultsSink` pattern. The binder reports
/// every symbol it declares or resolves into the sink as a side effect of normal
/// binding; batch compilation passes the no-op (zero cost, no branches), tooling
/// passes a collector that becomes the symbol-use index a SemanticModel / LSP
/// queries. The semantic model is a by-product of binding, never a second pipeline.
///
/// One method per symbol kind, each carrying the interned symbol (reference
/// identity → `FindReferences` is reference-equality), the occurrence span, and
/// whether the report is the declaration or a use. `OnUnitBound` closes each file
/// with its namespace + imports — the scope context `LookupSymbolsInScope` needs to
/// answer "what namespace-visible names are in view here".
public interface ISemanticSink
{
    void OnTypeResolved(TypeSymbol symbol, SourceSpan span, SymbolOccurrence occurrence);
    void OnMethodResolved(MethodSymbol symbol, SourceSpan span, SymbolOccurrence occurrence);
    void OnFieldResolved(FieldSymbol symbol, SourceSpan span, SymbolOccurrence occurrence);
    void OnLocalResolved(LocalSymbol symbol, SourceSpan span, SymbolOccurrence occurrence);
    void OnConstResolved(ConstSymbol symbol, SourceSpan span, SymbolOccurrence occurrence);
    void OnCaseResolved(CaseSymbol symbol, SourceSpan span, SymbolOccurrence occurrence);

    /// Closes a compilation unit: the file path, its namespace, and the namespaces
    /// it imports (`using "NS"`). The scope frame namespace-visible lookups read.
    void OnUnitBound(string file, string @namespace, IReadOnlyList<string> imports);
}

/// The batch default: discard everything. A sealed singleton so the JIT sees one
/// concrete no-op target on the hot path.
public sealed class NullSemanticSink : ISemanticSink
{
    public static readonly NullSemanticSink Instance = new();
    NullSemanticSink() { }
    public void OnTypeResolved(TypeSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) { }
    public void OnMethodResolved(MethodSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) { }
    public void OnFieldResolved(FieldSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) { }
    public void OnLocalResolved(LocalSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) { }
    public void OnConstResolved(ConstSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) { }
    public void OnCaseResolved(CaseSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) { }
    public void OnUnitBound(string file, string @namespace, IReadOnlyList<string> imports) { }
}

/// One resolved occurrence: the interned symbol, where it appears, and whether the
/// appearance is its declaration or a use. The common currency of every query.
public sealed record SymbolOccurrenceRecord(ISymbol Symbol, SourceSpan Span, SymbolOccurrence Occurrence);

/// The scope frame of a bound unit — the namespace and imports in view across the
/// whole file, the context `LookupSymbolsInScope` layers namespace-visible names on.
public sealed record UnitScope(string File, string Namespace, IReadOnlyList<string> Imports);

/// The tooling collector: accumulates every report in order. The raw material for
/// hover / go-to-def / find-refs once the SemanticModel queries land on top.
public sealed class CollectingSemanticSink : ISemanticSink
{
    readonly List<SymbolOccurrenceRecord> _occurrences = [];
    readonly List<UnitScope> _units = [];

    /// Every reported occurrence, declaration and use alike, in binding order.
    public IReadOnlyList<SymbolOccurrenceRecord> Occurrences => _occurrences;

    /// The per-file scope frames, in binding order.
    public IReadOnlyList<UnitScope> Units => _units;

    public void OnTypeResolved(TypeSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) =>
        _occurrences.Add(new SymbolOccurrenceRecord(symbol, span, occurrence));

    public void OnMethodResolved(MethodSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) =>
        _occurrences.Add(new SymbolOccurrenceRecord(symbol, span, occurrence));

    public void OnFieldResolved(FieldSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) =>
        _occurrences.Add(new SymbolOccurrenceRecord(symbol, span, occurrence));

    public void OnLocalResolved(LocalSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) =>
        _occurrences.Add(new SymbolOccurrenceRecord(symbol, span, occurrence));

    public void OnConstResolved(ConstSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) =>
        _occurrences.Add(new SymbolOccurrenceRecord(symbol, span, occurrence));

    public void OnCaseResolved(CaseSymbol symbol, SourceSpan span, SymbolOccurrence occurrence) =>
        _occurrences.Add(new SymbolOccurrenceRecord(symbol, span, occurrence));

    public void OnUnitBound(string file, string @namespace, IReadOnlyList<string> imports) =>
        _units.Add(new UnitScope(file, @namespace, imports));
}
