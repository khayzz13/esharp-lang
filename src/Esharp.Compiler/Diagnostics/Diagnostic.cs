namespace Esharp.Compiler.Diagnostics;

public sealed record Diagnostic(string FilePath, int Line, int Column, string Message)
{
    public override string ToString() => $"{FilePath}({Line},{Column}): error ES0001: {Message}";
}
