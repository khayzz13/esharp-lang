using System.Collections.Concurrent;
using System.Xml;

namespace Esharp.Metadata;

/// Reads sibling <c>&lt;AssemblyName&gt;.xml</c> files that accompany BCL and
/// NuGet assemblies, and surfaces their member documentation summaries for LSP
/// hover (WS7 of the language-server plan).
///
/// The XML format is the standard .NET doc-comment schema:
/// <c>&lt;doc&gt;&lt;members&gt;&lt;member name="T:Ns.Type"&gt;&lt;summary&gt;…&lt;/summary&gt;…</c>
///
/// This class is purely additive — a missing XML file or a malformed entry
/// produces null, never a diagnostic. The LSP hover falls back to the type
/// signature when no doc is available.
///
/// Thread-safety: all members are safe for concurrent reads. <see cref="TryLoadSibling"/>
/// uses a ConcurrentDictionary and a per-path lock to avoid double-parsing.
public sealed class MetadataXmlDocs
{
    // doc id → summary text. Populated on first TryLoadSibling for each path.
    readonly ConcurrentDictionary<string, string> _docs = new(StringComparer.Ordinal);

    // Tracks which XML files have been attempted (success or failure), so we
    // don't retry on every lookup.
    readonly ConcurrentDictionary<string, bool> _attempted = new(StringComparer.OrdinalIgnoreCase);

    /// Try to load the sibling XML file for a DLL at <paramref name="assemblyPath"/>.
    /// The sibling is <c>Path.ChangeExtension(assemblyPath, ".xml")</c>. No-op if
    /// the file doesn't exist or has already been loaded.
    public void TryLoadSibling(string assemblyPath)
    {
        var xmlPath = Path.ChangeExtension(assemblyPath, ".xml");
        if (!_attempted.TryAdd(xmlPath, true)) return;
        if (!File.Exists(xmlPath)) return;

        try { ParseXmlDocs(xmlPath); }
        catch { /* malformed XML — skip silently */ }
    }

    /// Look up the summary text for a member by its XML doc id
    /// (e.g. <c>"T:System.Collections.Generic.List`1"</c>,
    /// <c>"M:System.String.Concat(System.String,System.String)"</c>).
    /// Returns null when the doc file wasn't found or the member has no entry.
    public string? TryGetDoc(string? xmlDocId)
    {
        if (xmlDocId is null) return null;
        return _docs.TryGetValue(xmlDocId, out var doc) ? doc : null;
    }

    void ParseXmlDocs(string xmlPath)
    {
        using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = true,
        });

        string? currentMember = null;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.Name == "member":
                    currentMember = reader.GetAttribute("name");
                    break;

                case XmlNodeType.Element when reader.Name == "summary" && currentMember is not null:
                    var summary = reader.ReadElementContentAsString();
                    // Normalise: trim excess whitespace, collapse internal newlines.
                    summary = NormaliseSummary(summary);
                    if (!string.IsNullOrWhiteSpace(summary))
                        _docs[currentMember] = summary;
                    break;
            }
        }
    }

    static string NormaliseSummary(string raw)
    {
        // Replace runs of whitespace (including newlines / leading indentation
        // from the XML) with a single space, then trim the whole string.
        var parts = raw.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(p => p.Trim())).Trim();
    }
}
