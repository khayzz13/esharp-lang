namespace Esharp.Compiler.Syntax;

public abstract record SyntaxNode;

public abstract record MemberSyntax : SyntaxNode;

public abstract record StatementSyntax : SyntaxNode;

public abstract record ExpressionSyntax : SyntaxNode;

public sealed record CompilationUnitSyntax(string? ModuleName, IReadOnlyList<string> Imports, IReadOnlyList<MemberSyntax> Members) : SyntaxNode;

public sealed record TypeReferenceSyntax(string Name, bool ByRef = false) : SyntaxNode;

public sealed record FieldSyntax(string Name, TypeReferenceSyntax Type) : SyntaxNode;

public sealed record ParameterSyntax(string Name, TypeReferenceSyntax Type) : SyntaxNode;

public sealed record ChoiceCaseSyntax(string Name, string? PayloadName, TypeReferenceSyntax? PayloadType) : SyntaxNode;

public sealed record DataDeclarationSyntax(string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<string> Interfaces, IReadOnlyList<FieldSyntax> Fields) : MemberSyntax;

// protocol IGreeter { func greet(name: string) -> string }
public sealed record ProtocolMethodSyntax(string Name, IReadOnlyList<ParameterSyntax> Parameters, TypeReferenceSyntax ReturnType) : SyntaxNode;
public sealed record ProtocolDeclarationSyntax(string Name, IReadOnlyList<ProtocolMethodSyntax> Methods) : MemberSyntax;

// #derive equality, debug
public sealed record DeriveDirectiveSyntax(IReadOnlyList<string> Traits) : SyntaxNode;

public sealed record ChoiceDeclarationSyntax(string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<ChoiceCaseSyntax> Cases) : MemberSyntax;

public sealed record FunctionDeclarationSyntax(
    string Name,
    IReadOnlyList<string> TypeParameters,
    IReadOnlyList<ParameterSyntax> Parameters,
    TypeReferenceSyntax ReturnType,
    BlockStatementSyntax Body) : MemberSyntax;

public sealed record BlockStatementSyntax(IReadOnlyList<StatementSyntax> Statements) : StatementSyntax;

public sealed record VariableDeclarationStatementSyntax(
    bool Mutable,
    string Name,
    TypeReferenceSyntax? ExplicitType,
    ExpressionSyntax Initializer) : StatementSyntax;

public sealed record IfStatementSyntax(
    ExpressionSyntax Condition,
    StatementSyntax ThenStatement,
    StatementSyntax? ElseStatement) : StatementSyntax;

public sealed record ReturnStatementSyntax(ExpressionSyntax? Expression) : StatementSyntax;

public sealed record ExpressionStatementSyntax(ExpressionSyntax Expression) : StatementSyntax;

public sealed record AssignmentStatementSyntax(ExpressionSyntax Target, ExpressionSyntax Expression) : StatementSyntax;
public sealed record CompoundAssignmentStatementSyntax(ExpressionSyntax Target, SyntaxTokenKind Operator, ExpressionSyntax Value) : StatementSyntax;

public sealed record WhileStatementSyntax(ExpressionSyntax Condition, StatementSyntax Body) : StatementSyntax;

public sealed record ForEachStatementSyntax(string Identifier, ExpressionSyntax Collection, StatementSyntax Body) : StatementSyntax;

public sealed record LiteralExpressionSyntax(object? Value, string Text) : ExpressionSyntax;

public sealed record NameExpressionSyntax(string Name) : ExpressionSyntax;

public sealed record UnaryExpressionSyntax(SyntaxTokenKind OperatorKind, ExpressionSyntax Operand) : ExpressionSyntax;

public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxTokenKind OperatorKind,
    ExpressionSyntax Right) : ExpressionSyntax;

public sealed record MemberAccessExpressionSyntax(ExpressionSyntax Target, string MemberName) : ExpressionSyntax;

public sealed record CallExpressionSyntax(ExpressionSyntax Target, IReadOnlyList<ExpressionSyntax> Arguments) : ExpressionSyntax;

public sealed record ObjectInitializerFieldSyntax(string Name, ExpressionSyntax Value) : SyntaxNode;

public sealed record ObjectCreationExpressionSyntax(
    TypeReferenceSyntax Type,
    IReadOnlyList<ObjectInitializerFieldSyntax> Fields) : ExpressionSyntax;

public sealed record ParenthesizedExpressionSyntax(ExpressionSyntax Expression) : ExpressionSyntax;

public sealed record SpawnExpressionSyntax(BlockStatementSyntax Body) : ExpressionSyntax;

// let x = expr else { body }
public sealed record LetGuardStatementSyntax(string Name, TypeReferenceSyntax? ExplicitType, ExpressionSyntax Initializer, BlockStatementSyntax ElseBody) : StatementSyntax;

// match (expr: TypeName) { arms }  or  match expr { arms }
public sealed record MatchPatternSyntax(string? CaseName, string? BindingName, bool IsDefault) : SyntaxNode;
public sealed record MatchArmSyntax(MatchPatternSyntax Pattern, BlockStatementSyntax Body) : SyntaxNode;
public sealed record MatchStatementSyntax(ExpressionSyntax Subject, TypeReferenceSyntax? SubjectType, IReadOnlyList<MatchArmSyntax> Arms) : StatementSyntax;

// enum Direction { north, south, east, west }
public sealed record EnumDeclarationSyntax(string Name, IReadOnlyList<string> Cases) : MemberSyntax;

// chan<AuditEvent>(256)
public sealed record ChanCreationExpressionSyntax(TypeReferenceSyntax ElementType, ExpressionSyntax? Capacity) : ExpressionSyntax;

// .invalidCredentials  or  .accountLocked(args...)
public sealed record DotCaseExpressionSyntax(string CaseName, IReadOnlyList<ExpressionSyntax> Arguments) : ExpressionSyntax;

// defer { body }
public sealed record DeferStatementSyntax(BlockStatementSyntax Body) : StatementSyntax;

// func(a: int, b: int) -> bool { return a < b }
public sealed record FunctionLiteralExpressionSyntax(
    IReadOnlyList<ParameterSyntax> Parameters,
    TypeReferenceSyntax ReturnType,
    BlockStatementSyntax Body) : ExpressionSyntax;

// &funcName — address-of (function pointer)
public sealed record AddressOfExpressionSyntax(string FunctionName) : ExpressionSyntax;

// items[expr]
public sealed record IndexExpressionSyntax(ExpressionSyntax Target, ExpressionSyntax Index) : ExpressionSyntax;

// items[start..end], items[..end], items[start..]
public sealed record RangeExpressionSyntax(ExpressionSyntax Target, ExpressionSyntax? Start, ExpressionSyntax? End) : ExpressionSyntax;

// expr?  — error propagation / result unwrap
public sealed record TryUnwrapExpressionSyntax(ExpressionSyntax Inner) : ExpressionSyntax;

// await expr — async suspension
public sealed record AwaitExpressionSyntax(ExpressionSyntax Inner) : ExpressionSyntax;

// async let name = expr — structured concurrent binding
public sealed record AsyncLetStatementSyntax(string Name, TypeReferenceSyntax? ExplicitType, ExpressionSyntax Initializer) : StatementSyntax;

// select { .recv(binding, channel) { body } ... }
public sealed record SelectArmSyntax(string Kind, string? Binding, ExpressionSyntax? Channel, ExpressionSyntax? Value, BlockStatementSyntax Body) : SyntaxNode;
public sealed record SelectStatementSyntax(IReadOnlyList<SelectArmSyntax> Arms) : StatementSyntax;
