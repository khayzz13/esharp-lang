using Esharp.BoundTree;

namespace Esharp.Lowering;

/// <summary>
/// Lowers type and namespace member storage into its emitted declaration form.
/// </summary>
/// <remarks>
/// This is deliberately distinct from <c>Esharp.Syntax.PropertyLowering</c>. The
/// syntax pass creates source-level accessor declarations for computed getters and
/// custom setters. This bound pass owns the representation of bare members and
/// <c>let</c>/<c>var</c> properties: it normalizes every stored initializer to the
/// declared storage type before type emission creates a field or property backing
/// slot. Consequently, member initialization uses the same explicit box and
/// nullable-lift nodes as local initialization instead of leaving CodeGen to infer
/// a conversion from two unrelated shapes.
/// </remarks>
public sealed class PropertyLowering : IBoundTreePass
{
    public static readonly PropertyLowering Instance = new();

    public BoundProgram Lower(BoundProgram program, SynthesizedSymbolSink sink)
    {
        List<BoundCompilationUnit>? units = null;
        for (var i = 0; i < program.Units.Count; i++)
        {
            var unit = program.Units[i];
            var lowered = LowerUnit(unit);
            if (!ReferenceEquals(lowered, unit) && units is null)
                units = [.. program.Units.Take(i)];
            units?.Add(lowered);
        }
        return units is null ? program : program with { Units = units };
    }

    static BoundCompilationUnit LowerUnit(BoundCompilationUnit unit)
    {
        List<BoundMember>? members = null;
        for (var i = 0; i < unit.Members.Count; i++)
        {
            var member = unit.Members[i];
            var lowered = LowerMember(member);
            if (!ReferenceEquals(lowered, member) && members is null)
                members = [.. unit.Members.Take(i)];
            members?.Add(lowered);
        }
        return members is null ? unit : unit with { Members = members };
    }

    static BoundMember LowerMember(BoundMember member) => member switch
    {
        BoundDataDeclaration data => LowerData(data),
        BoundStaticFuncDeclaration block => LowerStaticFunc(block),
        BoundNamespaceStateDeclaration state => LowerNamespaceState(state),
        _ => member,
    };

    static BoundDataDeclaration LowerData(BoundDataDeclaration data)
    {
        var fields = LowerFields(data.Fields);
        return ReferenceEquals(fields, data.Fields) ? data : data with { Fields = fields };
    }

    static BoundStaticFuncDeclaration LowerStaticFunc(BoundStaticFuncDeclaration block)
    {
        var fields = LowerFields(block.Fields);
        return ReferenceEquals(fields, block.Fields) ? block : block with { Fields = fields };
    }

    static BoundNamespaceStateDeclaration LowerNamespaceState(BoundNamespaceStateDeclaration state)
    {
        var field = LowerField(state.Field);
        return ReferenceEquals(field, state.Field) ? state : state with { Field = field };
    }

    static IReadOnlyList<BoundField> LowerFields(IReadOnlyList<BoundField> fields)
    {
        List<BoundField>? lowered = null;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var replacement = LowerField(field);
            if (!ReferenceEquals(replacement, field) && lowered is null)
                lowered = [.. fields.Take(i)];
            lowered?.Add(replacement);
        }
        return lowered ?? fields;
    }

    static BoundField LowerField(BoundField field)
    {
        // Computed properties own no storage. Every other member declaration — a
        // bare field, stored let/var property, and static-func field — has one
        // storage write and therefore one declared-type conversion boundary.
        if (field.DefaultValue is null || field.IsComputedProperty)
            return field;

        var initializer = CoerceStore(field.DefaultValue, field.Type);
        return ReferenceEquals(initializer, field.DefaultValue)
            ? field
            : field with { DefaultValue = initializer };
    }

    internal static BoundExpression CoerceStore(BoundExpression value, BoundType target) =>
        ConversionClassification.Classify(value.Type, target) switch
        {
            ConversionKind.Box => BoundConversion.Box(value, target),
            ConversionKind.NullableWrap when target is NullableType nullable =>
                BoundConversion.WrapNullable(value, nullable),
            _ => value,
        };
}
