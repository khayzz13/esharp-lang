using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Esharp.FuzzTests.Execution;

/// Canonical, MVID/timestamp-free fingerprint of an emitted assembly: a full
/// textual dump of types, members, and IL bodies, hashed. Two compilations of
/// the same source must produce the same canonical hash — raw file bytes can't
/// be compared because Cecil stamps a fresh module GUID on every write.
internal static class AssemblyCanonicalizer
{
    public static string Hash(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        var dump = new StringBuilder(1 << 16);
        foreach (var type in module.Types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            DumpType(type, dump);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump.ToString())));
    }

    static void DumpType(TypeDefinition type, StringBuilder dump)
    {
        dump.Append("type ").Append(type.FullName)
            .Append(" attrs=").Append((uint)type.Attributes)
            .Append(" base=").Append(type.BaseType?.FullName ?? "-").Append('\n');

        foreach (var iface in type.Interfaces.OrderBy(i => i.InterfaceType.FullName, StringComparer.Ordinal))
            dump.Append("  implements ").Append(iface.InterfaceType.FullName).Append('\n');

        foreach (var field in type.Fields)
            dump.Append("  field ").Append(field.Name)
                .Append(':').Append(field.FieldType.FullName)
                .Append(" attrs=").Append((uint)field.Attributes).Append('\n');

        foreach (var property in type.Properties)
            dump.Append("  prop ").Append(property.Name).Append(':').Append(property.PropertyType.FullName).Append('\n');

        foreach (var method in type.Methods)
            DumpMethod(method, dump);

        foreach (var nested in type.NestedTypes.OrderBy(t => t.FullName, StringComparer.Ordinal))
            DumpType(nested, dump);
    }

    static void DumpMethod(MethodDefinition method, StringBuilder dump)
    {
        dump.Append("  method ").Append(method.FullName)
            .Append(" attrs=").Append((uint)method.Attributes)
            .Append(" implattrs=").Append((uint)method.ImplAttributes).Append('\n');

        if (!method.HasBody)
            return;

        var body = method.Body;
        dump.Append("    maxstack=").Append(body.MaxStackSize)
            .Append(" initlocals=").Append(body.InitLocals).Append('\n');
        foreach (var local in body.Variables)
            dump.Append("    local ").Append(local.Index).Append(':').Append(local.VariableType.FullName).Append('\n');
        foreach (var instruction in body.Instructions)
            dump.Append("    ").Append(instruction.Offset.ToString("x4"))
                .Append(' ').Append(instruction.OpCode.Name)
                .Append(' ').Append(RenderOperand(instruction.Operand)).Append('\n');
        foreach (var handler in body.ExceptionHandlers)
            dump.Append("    handler ").Append(handler.HandlerType)
                .Append(" try=").Append(handler.TryStart?.Offset ?? -1).Append('-').Append(handler.TryEnd?.Offset ?? -1)
                .Append(" handler=").Append(handler.HandlerStart?.Offset ?? -1).Append('-').Append(handler.HandlerEnd?.Offset ?? -1)
                .Append(" catch=").Append(handler.CatchType?.FullName ?? "-").Append('\n');
    }

    static string RenderOperand(object? operand) => operand switch
    {
        null => "",
        Instruction target => $"IL_{target.Offset:x4}",
        Instruction[] targets => string.Join(",", targets.Select(t => $"IL_{t.Offset:x4}")),
        MemberReference member => member.FullName,
        VariableDefinition variable => $"V_{variable.Index}",
        ParameterDefinition parameter => $"P_{parameter.Index}",
        string s => $"\"{s}\"",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => operand.ToString() ?? "",
    };
}
