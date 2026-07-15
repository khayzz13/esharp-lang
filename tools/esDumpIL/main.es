namespace EsDumpIL

using "Mono.Cecil"
using "Mono.Cecil.Cil"

// esDumpIL is deliberately an ordinary E# program. Program's constructor puts
// its instance state into a known shape, `main(args)` owns the command-line
// operation, and the static Program facet contains only presentation helpers.
// It is a real dogfood target for constructors, an args-taking class entry point,
// instance methods, and a static facet in one compact tool.

class Program {
    path: string
    typeFilter: string?
    methodFilter: string?
    validArguments: bool

    // The generated CLR entry point constructs Program(), then forwards the
    // normal CLI `args` array to its instance main. Initializing every part of
    // Program's state here makes the later command operation explicit.
    init() {
        self.path = ""
        self.typeFilter = nil
        self.methodFilter = nil
        self.validArguments = false
    }

    func main(args: string[]) -> int {
        if args.Length >= 1 {
            self.path = args[0]
            self.typeFilter = args.Length >= 2 ? args[1] : nil
            self.methodFilter = args.Length >= 3 ? args[2] : nil
            self.validArguments = true
        }
        if !self.validArguments {
            Program.printUsage()
            return 1
        }

        let assembly = AssemblyDefinition.ReadAssembly(self.path)
        if self.typeFilter == "--meta" {
            self.dumpMetadata(assembly)
            return 0
        }
        self.dumpModule(assembly)
        return 0
    }

    func dumpMetadata(assembly: AssemblyDefinition) {
        Console.WriteLine("=== assembly: {assembly.Name.FullName} ===")
        Console.WriteLine("-- references --")
        for reference in assembly.MainModule.AssemblyReferences.OrderBy((r) => r.FullName) {
            Console.WriteLine("   {reference.FullName}")
        }
        Console.WriteLine("-- assembly attributes --")
        for attribute in assembly.CustomAttributes {
            Console.WriteLine("   [{attribute.AttributeType.FullName}({Program.formatAttributeArguments(attribute)})]")
        }
    }

    func dumpModule(assembly: AssemblyDefinition) {
        let module = assembly.MainModule
        for type in module.Types {
            if self.typeFilter != nil && !type.Name.Contains(self.typeFilter) { continue }
            Console.WriteLine("=== {type.FullName} ===")
            for iface in type.Interfaces {
                Console.WriteLine("  : {iface.InterfaceType.FullName}")
            }
            for nested in type.NestedTypes {
                self.dumpType(nested, "  ")
            }
            self.dumpType(type, "")
        }
    }

    func dumpType(type: TypeDefinition, indent: string) {
        for method in type.Methods {
            if self.methodFilter != nil && !method.Name.Contains(self.methodFilter) { continue }
            Console.WriteLine("{indent}-- {method.FullName}")
            for attribute in method.CustomAttributes {
                Console.WriteLine("{indent}   [{attribute.AttributeType.FullName}({Program.formatAttributeArguments(attribute)})]")
            }
            if !method.HasBody { continue }
            for local in method.Body.Variables {
                Console.WriteLine("{indent}   .local [{local.Index}] {local.VariableType}")
            }
            if method.Body.HasExceptionHandlers {
                for handler in method.Body.ExceptionHandlers {
                    let catchType = handler.HandlerType == ExceptionHandlerType.Catch ? Program.formatMaybeType(handler.CatchType) : ""
                    Console.WriteLine("{indent}   .{handler.HandlerType.ToString().ToLower()} try [{Program.formatOffset(handler.TryStart)}..{Program.formatOffset(handler.TryEnd)}] handler [{Program.formatOffset(handler.HandlerStart)}..{Program.formatOffset(handler.HandlerEnd)}] type {catchType}")
                }
            }
            for instruction in method.Body.Instructions {
                Console.WriteLine("{indent}   IL_{Program.hex4(instruction.Offset)}: {instruction.OpCode.Name.PadRight(12)} {Program.formatOperand(instruction.Operand)}")
            }
        }
        for nested in type.NestedTypes {
            self.dumpType(nested, indent + "  ")
        }
    }
}

// Static utility surface: this is intentionally a facet, not a second unrelated
// helper type. It models the language's static-vs-instance split directly.
static Program {
    func printUsage() {
        Console.Error.WriteLine("usage: esDumpIL <path-to.dll> [type] [method]")
        Console.Error.WriteLine("       esDumpIL <path-to.dll> --meta   (assembly name, references, assembly-level attributes)")
    }

    func formatAttributeArguments(attribute: CustomAttribute) -> string {
        let parts = List<string>()
        for argument in attribute.ConstructorArguments {
            parts.Add(Program.formatAttributeValue(argument.Value))
        }
        return string.Join(", ", parts)
    }

    func formatAttributeValue(value: object?) -> string =
        match value {
            (text: string) => "\"{text}\""
            nil            => "null"
            default        => Convert.ToString(value) ?? ""
        }

    func formatMaybeType(type: TypeReference?) -> string = type == nil ? "" : type.FullName

    func formatOffset(instruction: Instruction?) -> string = instruction == nil ? "" : Program.hex4(instruction.Offset)

    func formatOperand(operand: object?) -> string =
        match operand {
            nil                              => ""
            (instruction: Instruction)       => "IL_{Program.hex4(instruction.Offset)}"
            (local: VariableDefinition)      => "V_{local.Index}"
            (parameter: ParameterDefinition) => "arg_{parameter.Index}({parameter.Name})"
            (method: GenericInstanceMethod)  => "{method.FullName} [decl={Program.formatType(method.DeclaringType)}; method-args={Program.formatTypes(method.GenericArguments)}]"
            (method: MethodReference)        => "{method.FullName} [decl={Program.formatType(method.DeclaringType)}]"
            (type: TypeReference)            => Program.formatType(type)
            (field: FieldReference)          => field.FullName
            (text: string)                   => "\"{text}\""
            default                          => Convert.ToString(operand) ?? ""
        }

    func formatTypes(types: IEnumerable<TypeReference>) -> string {
        let parts = List<string>()
        for type in types { parts.Add(Program.formatType(type)) }
        return string.Join(", ", parts)
    }

    func formatType(type: TypeReference) -> string =
        match type {
            (parameter: GenericParameter) => "{parameter.FullName} [owner={Program.formatOwner(parameter.Owner)}; pos={parameter.Position}]"
            (instance: GenericInstanceType) => "{instance.FullName} [element={instance.ElementType.FullName}; args={Program.formatTypes(instance.GenericArguments)}]"
            default => type.FullName
        }

    func formatOwner(owner: IGenericParameterProvider) -> string =
        match owner {
            (type: TypeReference)     => type.FullName
            (method: MethodReference) => method.FullName
            default                   => "<unknown>"
        }

    func hex4(value: int) -> string {
        let digits = "0123456789ABCDEF"
        let first = (value / 4096) % 16
        let second = (value / 256) % 16
        let third = (value / 16) % 16
        let fourth = value % 16
        return "{digits[first]}{digits[second]}{digits[third]}{digits[fourth]}"
    }
}
