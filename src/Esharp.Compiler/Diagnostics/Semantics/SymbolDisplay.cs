using Esharp.BoundTree;
using Esharp.Symbols;
// Seam: TypeResolver.TypeDisplayName still lives in Esharp.BoundTree until
// pillar 2 lands Esharp.Binder. Remove after integration.
using Esharp.BoundTree;
using Esharp.Symbols;

namespace Esharp.Diagnostics.Semantics;

/// <summary>
/// Human-readable renderings of a symbol for the LSP hover and completion surfaces.
/// Speaks E# source spelling (<c>TypeDisplayName</c>, never the C#-surface <c>EmitName</c>).
///
/// <list type="bullet">
///   <item><description><see cref="Describe"/> — the signature line, e.g. <c>func foo(x: i32) -> bool</c></description></item>
///   <item><description><see cref="DescribeKind"/> — the subtitle noun, e.g. <c>method on Foo</c></description></item>
///   <item><description><see cref="DescribeHover"/> — [Δ] full hover markdown: signature + XmlDoc block</description></item>
/// </list>
///
/// A stable surface the LSP binds to; the formatting deepens without changing the contract.
/// </summary>
public static class SymbolDisplay
{
    /// The one-line signature for a symbol — the hover headline and completion detail.
    public static string Describe(ISymbol symbol) => symbol switch
    {
        TypeSymbol t => DescribeType(t),
        MethodSymbol m => DescribeMethod(m),
        FieldSymbol f => $"{(f.IsProperty ? f.DeclaredMutable ? "var " : "let " : "")}{f.Name}: {Display(f.Bound)}",
        LocalSymbol l => $"{LocalPrefix(l)}{l.Name}: {Display(l.Type)}",
        ConstSymbol c => $"const {c.Name}",
        CaseSymbol c => $".{c.Name}",
        _ => symbol.Name,
    };

    /// A short noun for what KIND of thing a symbol is — the hover's subtitle so a
    /// reader can tell a method from a free function, a field from a parameter, at a
    /// glance.
    public static string DescribeKind(ISymbol symbol) => symbol switch
    {
        TypeSymbol t => t.TypeKind switch
        {
            TypeSymbolKind.Struct => "value type",
            TypeSymbolKind.Class => "reference type",
            TypeSymbolKind.Union => "choice (value union)",
            TypeSymbolKind.RefUnion => "choice (reference union)",
            TypeSymbolKind.Enum => "enum",
            TypeSymbolKind.Interface => "interface",
            TypeSymbolKind.StaticFunc => "static class",
            TypeSymbolKind.Delegate => "delegate",
            TypeSymbolKind.TypeParameter => "type parameter",
            TypeSymbolKind.NamespaceHost => "namespace",
            _ => "type",
        },
        MethodSymbol m when m.IsStatic && m.DeclaringType?.TypeKind == TypeSymbolKind.StaticFunc
            => $"static function on {m.DeclaringType!.Name}",
        MethodSymbol m when m.ReceiverKind != ReceiverKind.None
                            || (m.DeclaringType is { } dt && dt.TypeKind is TypeSymbolKind.Struct or TypeSymbolKind.Class
                                or TypeSymbolKind.Union or TypeSymbolKind.RefUnion or TypeSymbolKind.Interface
                                or TypeSymbolKind.External or TypeSymbolKind.Primitive or TypeSymbolKind.ExternalCSharp)
            => m.DeclaringType is { } owner ? $"method on {owner.Name}" : "method",
        MethodSymbol => "free function",
        FieldSymbol f when f.IsProperty && f.HasScopedPropertyLocation && f.HasDurablePropertyLocation
            => "scoped and durable location property",
        FieldSymbol f when f.IsProperty && f.HasScopedPropertyLocation => "scoped location property",
        FieldSymbol f when f.IsProperty && f.HasDurablePropertyLocation => "durable location property",
        FieldSymbol { IsProperty: true } => "property",
        FieldSymbol => "field",
        LocalSymbol l when l.IsParameter => "parameter",
        LocalSymbol l => l.IsAddressable ? "addressable local" : "non-addressable local",
        ConstSymbol => "constant",
        CaseSymbol => "choice case",
        _ => "",
    };

    static string LocalPrefix(LocalSymbol local) => local.IsParameter
        ? ""
        : local.Representation switch
        {
            LocalRepresentation.BareTypedValue => "",
            LocalRepresentation.ReadonlyLocation => "let ",
            LocalRepresentation.MutableLocation => "var ",
            _ => local.Mutable ? "var " : "let ",
        };

    /// <summary>
    /// [Δ] Full hover markdown block: the signature code fence, the kind subtitle,
    /// and the XmlDoc comment block when one is attached to the symbol.
    ///
    /// Structure:
    /// <code>
    /// ```esharp
    /// func foo(x: i32) -> bool
    /// ```
    /// _method on Bar_
    ///
    /// [doc text stripped of /// markers, preserving summary / param / returns structure]
    /// </code>
    ///
    /// The caller (LSP handler) passes this string directly into an LSP
    /// <c>MarkupContent { kind: "markdown" }</c> hover response.
    /// </summary>
    public static string DescribeHover(ISymbol symbol)
    {
        var sig = Describe(symbol);
        var kind = DescribeKind(symbol);
        var doc = symbol.XmlDoc;

        var sb = new System.Text.StringBuilder();
        sb.Append("```esharp\n");
        sb.Append(sig);
        sb.Append("\n```");

        if (!string.IsNullOrEmpty(kind))
        {
            sb.Append("\n_");
            sb.Append(kind);
            sb.Append('_');
        }

        if (!string.IsNullOrWhiteSpace(doc))
        {
            sb.Append("\n\n");
            sb.Append(RenderXmlDoc(doc));
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ type description

    static string DescribeType(TypeSymbol t)
    {
        var keyword = t.TypeKind switch
        {
            TypeSymbolKind.Struct => "struct",
            TypeSymbolKind.Class => "class",
            TypeSymbolKind.Union => "union",
            TypeSymbolKind.RefUnion => "ref union",
            TypeSymbolKind.Enum => "enum",
            TypeSymbolKind.Interface => "interface",
            TypeSymbolKind.StaticFunc => "static",
            TypeSymbolKind.Delegate => "delegate func",
            _ => "type",
        };
        return $"{keyword} {t.Name}{TypeParams(t.Arity)}";
    }

    static string DescribeMethod(MethodSymbol m)
    {
        // Void is explicit: `-> void`, never an implicit empty tail — the hover should
        // state the return even when it is nothing.
        var ret = m.ReturnType is null or VoidType ? " -> void" : $" -> {Display(m.ReturnType)}";
        var tps = m.TypeParameters.Count > 0 ? $"<{string.Join(", ", m.TypeParameters)}>" : "";

        // A receiver method renders in its declaration form — the receiver lifted into a
        // Go-style block, not shown as the leading parameter: `readonly func (c: Circle) m()`.
        if (m.ReceiverKind != ReceiverKind.None && m.DeclaredParameters.Count > 0)
        {
            var recvName = m.Decl?.Receiver?.Name ?? "self";
            var recvType = DisplayRef(m.DeclaredParameters[0]);
            var rest = string.Join(", ", m.DeclaredParameters.Skip(1).Select(DisplayRef));
            var kw = m.ReceiverKind == ReceiverKind.ReadonlyValue ? "readonly func" : "func";
            return $"{kw} ({recvName}: {recvType}) {m.Name}{tps}({rest}){ret}";
        }

        var args = string.Join(", ", m.DeclaredParameters.Select(DisplayRef));
        return $"func {m.Name}{tps}({args}){ret}";
    }

    // ------------------------------------------------------------------ XmlDoc rendering [Δ]

    /// Strip `///` prefixes and minimal XML structure for markdown hover display.
    /// <summary>foo</summary> → "foo" as a paragraph.
    /// <param name="x">desc</param> → "**x** — desc" bullet.
    /// <returns>desc</returns> → "**returns** desc" line.
    /// <remarks>text</remarks> → appended as-is.
    /// Everything else: inner text extracted, XML tags stripped.
    static string RenderXmlDoc(string xmlDoc)
    {
        // Strip `///` leading markers and normalize whitespace.
        var stripped = string.Join("\n",
            xmlDoc.Split('\n')
                  .Select(l => l.TrimStart())
                  .Select(l => l.StartsWith("///") ? l[3..].TrimStart() : l));

        // Wrap in a root so System.Xml can parse it.
        string xml;
        try
        {
            var wrapped = $"<root>{stripped}</root>";
            var doc = System.Xml.Linq.XDocument.Parse(wrapped, System.Xml.Linq.LoadOptions.None);
            var sb = new System.Text.StringBuilder();

            // <summary>
            var summary = doc.Root?.Element("summary")?.Value?.Trim();
            if (!string.IsNullOrEmpty(summary))
            {
                sb.Append(summary);
                sb.Append("\n\n");
            }

            // <remarks>
            var remarks = doc.Root?.Element("remarks")?.Value?.Trim();
            if (!string.IsNullOrEmpty(remarks))
            {
                sb.Append(remarks);
                sb.Append("\n\n");
            }

            // <param name="x">desc</param>
            var @params = doc.Root?.Elements("param").ToList();
            if (@params is { Count: > 0 })
            {
                foreach (var p in @params)
                {
                    var name = p.Attribute("name")?.Value ?? "?";
                    var desc = p.Value.Trim();
                    sb.Append($"- **{name}** — {desc}\n");
                }
                sb.Append('\n');
            }

            // <returns>
            var returns = doc.Root?.Element("returns")?.Value?.Trim();
            if (!string.IsNullOrEmpty(returns))
                sb.Append($"**returns** {returns}\n");

            xml = sb.ToString().TrimEnd();
        }
        catch
        {
            // Malformed XML: fall back to stripped plain text.
            xml = stripped.Trim();
        }

        return xml;
    }

    // ------------------------------------------------------------------ helpers

    static string TypeParams(int arity) =>
        arity == 0 ? "" : $"<{string.Join(", ", Enumerable.Range(0, arity).Select(i => $"T{i + 1}"))}>";

    static string Display(BoundType? t) => t is null ? "?" : BoundTypeDisplay.Name(t);

    static string DisplayRef(TypeRef r)
    {
        var name = r.Symbol.Name;
        return r.Modifier switch
        {
            TypeRefModifier.HeapPointer or TypeRefModifier.ByRef => "*" + name,
            TypeRefModifier.Nullable => name + "?",
            TypeRefModifier.Array => name + "[]",
            _ => name,
        };
    }
}
