namespace Esharp.FuzzTests.Generation;

/// Scale knobs for generated programs. Ci keeps cases small and fast; Soak
/// widens everything; Boundary deliberately drives IL-format edges — &gt;255
/// locals (fat ldloc), branch offsets past ±127 (br.s overflow, the
/// ShortenOpcodes risk), deep expression nesting, and wide metadata.
internal sealed record GeneratorProfile(
    int GoStatements,
    int ExprDepth,
    int HelperCount,
    int DataTypeCount,
    int IslandCount,
    int LoopBoundMax,
    bool Boundary)
{
    public static readonly GeneratorProfile Ci = new(
        GoStatements: 10, ExprDepth: 3, HelperCount: 3, DataTypeCount: 2,
        IslandCount: 4, LoopBoundMax: 6, Boundary: false);

    public static readonly GeneratorProfile Soak = new(
        GoStatements: 24, ExprDepth: 4, HelperCount: 6, DataTypeCount: 3,
        IslandCount: 8, LoopBoundMax: 12, Boundary: false);

    public static readonly GeneratorProfile BoundaryProfile = new(
        GoStatements: 40, ExprDepth: 7, HelperCount: 4, DataTypeCount: 2,
        IslandCount: 5, LoopBoundMax: 8, Boundary: true);
}
