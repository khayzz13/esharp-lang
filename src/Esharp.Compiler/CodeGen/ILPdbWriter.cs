using Mono.Cecil;
using Mono.Cecil.Cil;
using Esharp.Syntax;

namespace Esharp.CodeGen;

/// <summary>
/// Applies collected sequence point anchors to method debug information
/// and configures portable PDB emission on assembly write.
/// </summary>
public static class ILPdbWriter
{
    // Stable GUID for the E# language in PDB documents.
    static readonly Guid EsharpLanguageGuid = new("e5740000-e574-0000-e574-000000000001");

    /// <summary>
    /// Apply sequence points from an ILMethodEmitter to the method's DebugInformation.
    /// Call this AFTER ILOptimizer.ShortenOpcodes — Nop anchors are stable across optimization.
    /// </summary>
    public static void ApplySequencePoints(
        MethodDefinition method,
        IReadOnlyList<(Instruction Anchor, SourceSpan Span)> sequencePoints,
        Dictionary<string, Document> documentCache)
    {
        if (sequencePoints.Count == 0) return;

        foreach (var (anchor, span) in sequencePoints)
        {
            if (!span.IsValid) continue;

            if (!documentCache.TryGetValue(span.File, out var doc))
            {
                doc = new Document(span.File)
                {
                    LanguageGuid = EsharpLanguageGuid,
                    Type = DocumentType.Text,
                    HashAlgorithm = DocumentHashAlgorithm.SHA256,
                };
                documentCache[span.File] = doc;
            }

            var sp = new SequencePoint(anchor, doc)
            {
                StartLine = span.Line,
                StartColumn = span.Column,
                EndLine = span.Line,
                EndColumn = span.Column + 1,
            };
            method.DebugInformation.SequencePoints.Add(sp);
        }
    }

    /// <summary>
    /// Write an assembly with portable PDB symbols.
    /// Loads PortablePdbWriterProvider dynamically from the Mono.Cecil.Pdb assembly
    /// that ships alongside Mono.Cecil in the NuGet package.
    /// </summary>
    public static void WriteWithPdb(AssemblyDefinition assembly, string outputPath)
    {
        // PortablePdbWriterProvider is internal in Mono.Cecil.dll. Access it via
        // reflection on the main Cecil assembly (it's the type that
        // DefaultSymbolWriterProvider delegates to for portable PDB format).
        var cecilAsm = typeof(AssemblyDefinition).Assembly;
        var providerType = cecilAsm.GetType("Mono.Cecil.Cil.PortablePdbWriterProvider")
            ?? throw new InvalidOperationException("PortablePdbWriterProvider not found in Mono.Cecil assembly");
        var provider = (ISymbolWriterProvider)Activator.CreateInstance(providerType)!;

        assembly.Write(outputPath, new WriterParameters
        {
            WriteSymbols = true,
            SymbolWriterProvider = provider,
        });
    }

    /// <summary>
    /// Write an assembly without PDB symbols.
    /// </summary>
    public static void WriteWithoutPdb(AssemblyDefinition assembly, string outputPath)
    {
        assembly.Write(outputPath);
    }
}
