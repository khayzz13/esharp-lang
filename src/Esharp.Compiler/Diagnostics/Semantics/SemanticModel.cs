using Esharp.BoundTree;
using Esharp.Symbols;
using Esharp.Syntax;
// Seam: ExternalSymbols and TypeResolver still live in Esharp.Compiler until
// C3 lands Esharp.Metadata (MetadataReader-backed). Remove these after integration.
using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Diagnostics.Semantics;

/// One reference to a symbol: where it appears and whether it is the declaration
/// or a use. The currency `FindReferences` returns.
public sealed record SymbolUse(ISymbol Symbol, SourceSpan Span, SymbolOccurrence Occurrence);

// ============================================================================
// [Δ] SemanticModel — per-file + interval index (kills O(all) scans)
//
// Architecture:
//   - _byFile:  file → sorted array of all occurrences in that file, sorted by Span.Start.
//               Used by OccurrencesIn (return the array), DeclarationsIn (filter), and
//               GetSymbolAt (binary-search the candidates that contain position).
//
//   - _bySymbol: identity-keyed dict (RuntimeHelpers.GetHashCode) → list of SymbolUse.
//               FindReferences is a single dict lookup instead of an O(all) scan.
//
//   - _localIndex: per-file sorted array of LocalSymbol declarations, sorted by
//               BlockSpan.Start. LookupSymbolsInScope binary-searches for locals
//               whose block contains the cursor. This is O(log n + k) not O(all).
//
//   - _unitByFile: file → UnitScope (the namespace + imports frame).
//               O(1) lookup for LookupSymbolsInScope's namespace-visible sweep.
//
//   - _nsDeclarations: namespace → list of namespace-visible symbols (TypeSymbol / top-level
//               MethodSymbol). LookupSymbolsInScope merges the visible namespaces' buckets
//               without scanning every occurrence.
//
// Construction is O(n log n) (sort per file). All queries are O(log n + k) or O(1 + k)
// where k is the result size — no O(all-occurrences-in-program) scans.
// ============================================================================
public sealed class SemanticModel
{
    // Per-file sorted occurrence array — the primary index.
    readonly Dictionary<string, SymbolOccurrenceRecord[]> _byFile;

    // Identity-keyed occurrence lists for FindReferences — O(1) lookup.
    readonly Dictionary<int, List<SymbolUse>> _bySymbol;

    // Per-file sorted local declarations for scope lookup.
    readonly Dictionary<string, LocalDecl[]> _localIndex;

    // File → UnitScope (namespace + imports).
    readonly Dictionary<string, UnitScope> _unitByFile;

    // Namespace → namespace-visible declarations (TypeSymbol / top-level MethodSymbol).
    readonly Dictionary<string, List<ISymbol>> _nsDeclarations;

    // External symbol resolver (Esharp.Metadata.ExternalSymbols via the IExternalSymbols
    // contract) + runtime-Type resolver. Optional — absent means null for external receivers.
    readonly IExternalSymbols? _externals;
    readonly Func<BoundType, Type?>? _runtime;

    // ------------------------------------------------------------------ construction

    public SemanticModel(CollectingSemanticSink sink,
        IExternalSymbols? externals = null, Func<BoundType, Type?>? runtime = null)
        : this(sink.Occurrences, sink.Units, externals, runtime) { }

    public SemanticModel(IReadOnlyList<SymbolOccurrenceRecord> occurrences, IReadOnlyList<UnitScope> units,
        IExternalSymbols? externals = null, Func<BoundType, Type?>? runtime = null)
    {
        _externals = externals;
        _runtime = runtime;

        // Dedupe: a type annotation can be resolved in multiple passes; keep one per
        // (symbol-identity, file, start, end, occurrence).
        var seen = new HashSet<(int id, string file, int s, int e, SymbolOccurrence occ)>();
        var deduped = new List<SymbolOccurrenceRecord>(occurrences.Count);
        foreach (var o in occurrences)
        {
            // Synthesized/compiler-only symbols legitimately carry SourceSpan.default.
            // They have no document position, so they must not enter the per-file source
            // indexes (the default File is null at runtime despite the non-nullable type).
            if (!o.Span.IsValid || string.IsNullOrEmpty(o.Span.File)) continue;
            var key = (System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Symbol),
                       o.Span.File, o.Span.Start, o.Span.End, o.Occurrence);
            if (seen.Add(key)) deduped.Add(o);
        }

        // Build per-file buckets and sort by Start (stable, so source order is preserved).
        var fileBuckets = new Dictionary<string, List<SymbolOccurrenceRecord>>(StringComparer.Ordinal);
        foreach (var o in deduped)
        {
            if (!fileBuckets.TryGetValue(o.Span.File, out var bucket))
                fileBuckets[o.Span.File] = bucket = [];
            bucket.Add(o);
        }
        _byFile = new(StringComparer.Ordinal);
        foreach (var (file, bucket) in fileBuckets)
        {
            bucket.Sort((a, b) => a.Span.Start != b.Span.Start
                ? a.Span.Start.CompareTo(b.Span.Start)
                : a.Span.End.CompareTo(b.Span.End));
            _byFile[file] = bucket.ToArray();
        }

        // Build by-symbol index using reference identity.
        _bySymbol = new(capacity: deduped.Count / 2);
        foreach (var o in deduped)
        {
            var id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o.Symbol);
            if (!_bySymbol.TryGetValue(id, out var list))
                _bySymbol[id] = list = [];
            list.Add(new SymbolUse(o.Symbol, o.Span, o.Occurrence));
        }

        // Build local index per file.
        var localBuckets = new Dictionary<string, List<LocalDecl>>(StringComparer.Ordinal);
        foreach (var o in deduped)
        {
            if (o.Occurrence != SymbolOccurrence.Declaration) continue;
            if (o.Symbol is not LocalSymbol local) continue;
            var block = local.BlockSpan;
            var declFile = block.End > block.Start ? block.File : local.Span.File;
            if (string.IsNullOrEmpty(declFile)) continue;
            if (!localBuckets.TryGetValue(declFile, out var lb))
                localBuckets[declFile] = lb = [];
            lb.Add(new LocalDecl(local, block));
        }
        _localIndex = new(StringComparer.Ordinal);
        foreach (var (file, lb) in localBuckets)
        {
            lb.Sort((a, b) => a.BlockSpan.Start.CompareTo(b.BlockSpan.Start));
            _localIndex[file] = lb.ToArray();
        }

        // Unit scope index.
        _unitByFile = new(StringComparer.Ordinal);
        foreach (var u in units) _unitByFile[u.File] = u;

        // Namespace-visible declarations: TypeSymbol and top-level MethodSymbol.
        _nsDeclarations = new(StringComparer.Ordinal);
        foreach (var o in deduped)
        {
            if (o.Occurrence != SymbolOccurrence.Declaration) continue;
            string? ns = o.Symbol switch
            {
                TypeSymbol ts => ts.Namespace ?? "",
                MethodSymbol ms when ms.DeclaringType?.TypeKind == TypeSymbolKind.NamespaceHost
                    => ms.DeclaringType.Namespace ?? "",
                FieldSymbol fs when fs.DeclaringType.TypeKind == TypeSymbolKind.NamespaceHost
                    => fs.DeclaringType.Namespace ?? "",
                _ => null,
            };
            if (ns is null) continue;
            if (!_nsDeclarations.TryGetValue(ns, out var nsList))
                _nsDeclarations[ns] = nsList = [];
            nsList.Add(o.Symbol);
        }
    }

    // ------------------------------------------------------------------ public queries

    /// The narrowest symbol occurrence whose span contains <paramref name="position"/> in
    /// <paramref name="file"/> — what hover and go-to-definition resolve a cursor to.
    /// Narrowest wins so a member access inside a larger statement returns the member.
    ///
    /// Implementation: binary-search the file's sorted occurrence array for candidates
    /// whose Start ≤ position, then scan backward until Start > position — O(log n + k).
    public ISymbol? GetSymbolAt(string file, int position)
    {
        if (!_byFile.TryGetValue(file, out var arr)) return null;

        // Binary-search for the last occurrence with Start ≤ position.
        int lo = 0, hi = arr.Length - 1, pivot = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (arr[mid].Span.Start <= position) { pivot = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (pivot < 0) return null;

        // Walk backward collecting candidates that contain position; pick narrowest.
        SymbolOccurrenceRecord? best = null;
        for (int i = pivot; i >= 0 && arr[i].Span.End > arr[i].Span.Start; i--)
        {
            var o = arr[i];
            if (o.Span.Start > position) continue;   // sorted, shouldn't happen going backward
            if (o.Span.End <= position) continue;      // doesn't contain position
            if (best is null || Width(o.Span) < Width(best.Span)) best = o;
            // Once the span is strictly narrower than anything we've seen, we can stop — all
            // earlier entries have Start ≤ o.Start so their spans can only be equal or wider.
        }
        return best?.Symbol;
    }

    /// Every reference to <paramref name="symbol"/> — declaration and all uses — by
    /// reference identity over the interned symbol. O(1) dict lookup + list copy.
    public IReadOnlyList<SymbolUse> FindReferences(ISymbol symbol)
    {
        var id = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(symbol);
        if (!_bySymbol.TryGetValue(id, out var list)) return [];
        // Filter by actual reference identity to handle (unlikely) hash collisions.
        var result = new List<SymbolUse>(list.Count);
        foreach (var u in list)
            if (ReferenceEquals(u.Symbol, symbol)) result.Add(u);
        return result;
    }

    /// Symbols visible at <paramref name="position"/> in <paramref name="file"/>:
    /// in-scope locals/parameters + namespace-visible types and free functions.
    ///
    /// Locals: binary-search the per-file local index for blocks containing position.
    /// Namespace-visible: merge _nsDeclarations buckets for the unit's namespace + imports.
    /// No O(all) scans.
    public IReadOnlyList<ISymbol> LookupSymbolsInScope(string file, int position)
    {
        var result = new List<ISymbol>();
        var seen = new HashSet<ISymbol>(ReferenceEqualityComparer.Instance);

        // Locals and parameters via the per-file local index.
        if (_localIndex.TryGetValue(file, out var locals))
        {
            // Binary-search: blocks sorted by Start; find first with Start ≤ position.
            int lo = 0, hi = locals.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (locals[mid].BlockSpan.Start <= position) lo = mid + 1;
                else hi = mid - 1;
            }
            // Walk backward over all blocks starting before position; check End.
            for (int i = hi; i >= 0; i--)
            {
                var d = locals[i];
                var block = d.BlockSpan;
                bool inBlock = block.End > block.Start
                    ? position >= block.Start && position <= block.End
                    : true; // no block range — fall back to declaration-precedes check
                if (!inBlock) continue;
                if (!d.Local.IsParameter && position < d.Local.Span.Start) continue;
                if (seen.Add(d.Local)) result.Add(d.Local);
            }
        }

        // Namespace-visible declarations.
        if (_unitByFile.TryGetValue(file, out var unit))
        {
            var visibleNs = new HashSet<string>(StringComparer.Ordinal) { unit.Namespace };
            foreach (var imp in unit.Imports) visibleNs.Add(imp);
            foreach (var ns in visibleNs)
            {
                if (!_nsDeclarations.TryGetValue(ns, out var nsSymbols)) continue;
                foreach (var sym in nsSymbols)
                    if (seen.Add(sym)) result.Add(sym);
            }
            // Also include symbols in the global bucket (null namespace stored as "").
            if (!visibleNs.Contains("") && _nsDeclarations.TryGetValue("", out var globalSymbols))
                foreach (var sym in globalSymbols)
                    if (seen.Add(sym)) result.Add(sym);
        }

        return result;
    }

    /// Every declaration in <paramref name="file"/>, in source order — the LSP
    /// documentSymbol surface. Callers filter by kind (a document outline drops locals).
    public IReadOnlyList<SymbolUse> DeclarationsIn(string file)
    {
        if (!_byFile.TryGetValue(file, out var arr)) return [];
        var decls = new List<SymbolUse>();
        foreach (var o in arr) // already sorted by Start
            if (o.Occurrence == SymbolOccurrence.Declaration)
                decls.Add(new SymbolUse(o.Symbol, o.Span, o.Occurrence));
        return decls;
    }

    /// Every symbol occurrence in <paramref name="file"/> — declarations AND uses —
    /// in source order. The semantic-tokens surface; each symbol maps to a token type.
    /// Returns the pre-sorted array segment as a new list (shallow copy).
    public IReadOnlyList<SymbolUse> OccurrencesIn(string file)
    {
        if (!_byFile.TryGetValue(file, out var arr)) return [];
        var uses = new List<SymbolUse>(arr.Length);
        foreach (var o in arr)
            if (o.Span.End > o.Span.Start)
                uses.Add(new SymbolUse(o.Symbol, o.Span, o.Occurrence));
        return uses;
    }

    /// The type behind a symbol — the member surface for dot-completion after `x.`.
    /// Null when the type is external (no in-assembly symbol) or unresolved.
    public TypeSymbol? GetTypeOf(ISymbol symbol)
    {
        var t = symbol switch
        {
            TypeSymbol ts => ts,
            LocalSymbol l => SymbolBehind(l.Type) ?? ExternalBehind(l.Type),
            FieldSymbol f => SymbolBehind(f.Bound) ?? ExternalBehind(f.Bound),
            MethodSymbol m => SymbolBehind(m.ReturnType) ?? ExternalBehind(m.ReturnType),
            _ => null,
        };
        // External receivers: populate member surface on demand.
        if (t is not null && t.TypeKind is TypeSymbolKind.External or TypeSymbolKind.Primitive)
            _externals?.EnsureMembers(t);
        return t;
    }

    // ------------------------------------------------------------------ internals

    // Small record threading a LocalSymbol + its block span through the index.
    readonly record struct LocalDecl(LocalSymbol Local, SourceSpan BlockSpan);

    TypeSymbol? ExternalBehind(BoundType? t) => t switch
    {
        null => null,
        HeapPointerBoundType hp => ExternalBehind(hp.Inner),
        NullableType n => ExternalBehind(n.Inner),
        ExternalType or PrimitiveType =>
            _externals is not null && _runtime?.Invoke(t) is { } rt
                ? _externals.ForType(rt, populateMembers: true)
                : null,
        _ => null,
    };

    static TypeSymbol? SymbolBehind(BoundType? t) => t switch
    {
        DataType d => d.Symbol,
        ChoiceType c => c.Symbol,
        EnumType e => e.Symbol,
        InterfaceType i => i.Symbol,
        NamedDelegateType nd => nd.Symbol,
        StaticFuncType s => s.Symbol,
        ExternalCSharpType x => x.Symbol,
        HeapPointerBoundType hp => SymbolBehind(hp.Inner),
        NullableType n => SymbolBehind(n.Inner),
        _ => null,
    };

    static int Width(SourceSpan s) => s.End - s.Start;
}
