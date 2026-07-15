using Esharp.Syntax;

namespace Esharp.Syntax;

/// <summary>
/// Lowers a `struct`/`class` declaration's property accessors to real `get_<name>` /
/// `set_<name>` methods, so the rest of the pipeline binds and emits them exactly like any
/// in-body method (the emitter then attaches the <c>PropertyDefinition</c> and, for a stored
/// property, the private <c>&lt;name&gt;k__BackingField</c> + trivial accessors).
///
/// Two accessor forms need a source-level body and so are synthesized here; the trivial
/// stored accessors (`get_x`/`set_x` over the backing field) are emitted directly by
/// <see cref="ILEmit.ILEmitter"/> and need no method here:
///
///   • computed getter — `let x: T =&gt; expr` → `get_x(self: T) -&gt; T = expr` (recomputed,
///     no backing field);
///   • custom setter — `var x: T { set(v) =&gt; valueExpr }` → `set_x(self: T, v: T) { self.x = valueExpr }`.
///     The body is a VALUE expression stored to `x`; the in-type write `self.x` targets the
///     backing field (only a cross-type `obj.x = v` routes through `set_x`), so the setter
///     never re-enters itself.
///
/// Runs at the close of <c>ParseDataDeclaration</c>, so a nested type — parsed through its own
/// <c>ParseDataDeclaration</c> — is already lowered by the time the enclosing declaration is built.
/// </summary>
static class PropertyLowering
{
    public static DataDeclarationSyntax Lower(DataDeclarationSyntax decl)
    {
        if (!decl.Fields.Any(f => f.Property is not null)) return decl;

        var selfType = new NamedTypeSyntax(decl.Name);
        var added = new List<FunctionDeclarationSyntax>();

        foreach (var field in decl.Fields)
        {
            if (field.Property is not { } prop) continue;
            // A field's effective visibility is its explicit `pub`/`priv`, else the
            // declaration's — the accessor methods inherit it (matches the old inline form).
            var accessorPublic = field.IsPublic ?? decl.IsPublic;

            if (prop.ComputedGetter is { } getterExpr)
            {
                var body = new BlockStatementSyntax(new StatementSyntax[] { new ReturnStatementSyntax(getterExpr) });
                added.Add(new FunctionDeclarationSyntax(
                    accessorPublic, "get_" + field.Name, Array.Empty<string>(),
                    new List<ParameterSyntax> { new("self", selfType) }, field.Type, body,
                    Array.Empty<AttributeSyntax>(), HasExplicitReturnType: true) { IsTypeBodyMethod = true });
            }

            if (prop.SetterBody is { } setterBody)
            {
                var assign = new AssignmentStatementSyntax(
                    new MemberAccessExpressionSyntax(new NameExpressionSyntax("self"), field.Name),
                    setterBody);
                var body = new BlockStatementSyntax(new StatementSyntax[] { assign });
                added.Add(new FunctionDeclarationSyntax(
                    accessorPublic, "set_" + field.Name, Array.Empty<string>(),
                    new List<ParameterSyntax> { new("self", selfType), new(prop.SetterParam ?? "value", field.Type) },
                    new NamedTypeSyntax("void"), body,
                    Array.Empty<AttributeSyntax>(), HasExplicitReturnType: false) { IsTypeBodyMethod = true });
            }
        }

        if (added.Count == 0) return decl;
        var methods = decl.Methods is null ? added : decl.Methods.Concat(added).ToList();
        return decl with { Methods = methods };
    }
}
