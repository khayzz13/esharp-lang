using Esharp.Compiler.Syntax;

namespace Esharp.Compiler.Binding;

// === Top-level ===

public sealed record BoundCompilationUnit(string? ModuleName, IReadOnlyList<string> Imports, IReadOnlyList<BoundMember> Members);

// === Members ===

public abstract record BoundMember;

public sealed record BoundDataDeclaration(string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<string> Interfaces, IReadOnlyList<string> DeriveTraits, IReadOnlyList<BoundField> Fields, IReadOnlyList<BoundFunctionDeclaration> InstanceMethods) : BoundMember;
public sealed record BoundProtocolDeclaration(string Name, IReadOnlyList<BoundProtocolMethod> Methods) : BoundMember;
public sealed record BoundProtocolMethod(string Name, IReadOnlyList<BoundParameter> Parameters, BoundType ReturnType);
public sealed record BoundChoiceDeclaration(string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<BoundChoiceCase> Cases) : BoundMember;
public sealed record BoundEnumDeclaration(string Name, IReadOnlyList<string> Cases) : BoundMember;
public sealed record BoundFunctionDeclaration(string Name, IReadOnlyList<string> TypeParameters, IReadOnlyList<BoundParameter> Parameters, BoundType ReturnType, BoundBlockStatement Body, bool HasAwait = false) : BoundMember;

// === Statements ===

public abstract record BoundStatement;

public sealed record BoundBlockStatement(IReadOnlyList<BoundStatement> Statements) : BoundStatement;
public sealed record BoundVariableDeclaration(bool Mutable, string Name, BoundType DeclaredType, BoundExpression Initializer) : BoundStatement;
public sealed record BoundLetGuard(string Name, BoundType DeclaredType, BoundExpression Initializer, BoundBlockStatement ElseBody) : BoundStatement;
public sealed record BoundAssignment(BoundExpression Target, BoundExpression Value) : BoundStatement;
public sealed record BoundCompoundAssignment(BoundExpression Target, SyntaxTokenKind Op, BoundExpression Value) : BoundStatement;
public sealed record BoundIfStatement(BoundExpression Condition, BoundStatement Then, BoundStatement? Else) : BoundStatement;
public sealed record BoundWhileStatement(BoundExpression Condition, BoundStatement Body) : BoundStatement;
public sealed record BoundForEachStatement(string Identifier, BoundExpression Collection, BoundStatement Body) : BoundStatement;
public sealed record BoundReturnStatement(BoundExpression? Expression) : BoundStatement;
public sealed record BoundExpressionStatement(BoundExpression Expression) : BoundStatement;
public sealed record BoundMatchStatement(BoundExpression Subject, BoundType SubjectType, IReadOnlyList<BoundMatchArm> Arms) : BoundStatement;
public sealed record BoundDeferStatement(BoundBlockStatement Body) : BoundStatement;

// === Expressions ===

public abstract record BoundExpression(BoundType Type);

public sealed record BoundLiteralExpression(object? Value, string Text, BoundType Type) : BoundExpression(Type);
public sealed record BoundNameExpression(string Name, BoundType Type) : BoundExpression(Type);
public sealed record BoundUnaryExpression(SyntaxTokenKind Op, BoundExpression Operand, BoundType Type) : BoundExpression(Type);
public sealed record BoundBinaryExpression(BoundExpression Left, SyntaxTokenKind Op, BoundExpression Right, BoundType Type) : BoundExpression(Type);
public sealed record BoundMemberAccessExpression(BoundExpression Target, string MemberName, BoundType Type) : BoundExpression(Type);
public sealed record BoundCallExpression(BoundExpression Target, IReadOnlyList<BoundExpression> Arguments, BoundType Type) : BoundExpression(Type);
public sealed record BoundObjectCreationExpression(BoundType ObjectType, IReadOnlyList<BoundFieldInit> Fields) : BoundExpression(ObjectType);
public sealed record BoundParenthesizedExpression(BoundExpression Inner) : BoundExpression(Inner.Type);
public sealed record BoundIndexExpression(BoundExpression Target, BoundExpression Index, BoundType Type) : BoundExpression(Type);
public sealed record BoundRangeExpression(BoundExpression Target, BoundExpression? Start, BoundExpression? End, BoundType Type) : BoundExpression(Type);
public sealed record BoundSpawnExpression(BoundBlockStatement Body) : BoundExpression(new ExternalType("Job"));
public sealed record BoundChanCreationExpression(BoundType ElementType, BoundExpression? Capacity) : BoundExpression(new ChanType(ElementType));
public sealed record BoundDotCaseExpression(string CaseName, string ResolvedTypeName, IReadOnlyList<BoundExpression> Arguments, BoundType Type) : BoundExpression(Type);
public sealed record BoundTryUnwrapExpression(BoundExpression Inner, BoundType UnwrappedType, string TempName) : BoundExpression(UnwrappedType);

// &funcName — function pointer
public sealed record BoundAddressOfExpression(string FunctionName, IReadOnlyList<BoundType> ParameterTypes, BoundType ReturnType)
    : BoundExpression(new FunctionPointerType(ParameterTypes, ReturnType));

// func(a: int) -> bool { ... }  — anonymous function literal
public sealed record BoundFunctionLiteralExpression(IReadOnlyList<BoundParameter> Parameters, BoundType ReturnType, BoundBlockStatement Body) : BoundExpression(new ExternalType("var"));

// ok(value) / error(value) resolved against Result<T,E> return type
public sealed record BoundResultCallExpression(bool IsOk, BoundExpression Argument, BoundType OkType, BoundType ErrorType) : BoundExpression(new ResultType(OkType, ErrorType));

// === Supporting ===

public sealed record BoundField(string Name, BoundType Type);
public sealed record BoundParameter(string Name, BoundType Type, bool ByRef);
public sealed record BoundChoiceCase(string Name, string? PayloadName, BoundType? PayloadType);
public sealed record BoundFieldInit(string Name, BoundExpression Value);
public sealed record BoundMatchArm(BoundMatchPattern Pattern, BoundBlockStatement Body);
public sealed record BoundMatchPattern(string? CaseName, string? BindingName, BoundType? BindingType, bool IsDefault);

// await expr — result type is T extracted from Task<T>/ValueTask<T>, or var for unresolved
public sealed record BoundAwaitExpression(BoundExpression Inner, BoundType ResultType) : BoundExpression(ResultType);

// async let name = expr — structured concurrent binding
public sealed record BoundAsyncLetStatement(string Name, BoundType DeclaredType, BoundExpression Initializer) : BoundStatement;

// select { arms }
public sealed record BoundSelectArm(string Kind, string? Binding, BoundType? BindingType, BoundExpression? Channel, BoundExpression? Value, BoundBlockStatement Body);
public sealed record BoundSelectStatement(IReadOnlyList<BoundSelectArm> Arms) : BoundStatement;
