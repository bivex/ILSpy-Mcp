namespace ILSpy.Mcp.Domain.Models;

/// <summary>
/// Information about a .NET type.
/// </summary>
public sealed record TypeInfo
{
    public required string FullName { get; init; }
    public string? Namespace { get; init; }
    public required string ShortName { get; init; }
    public TypeKind Kind { get; init; }
    public Accessibility Accessibility { get; init; }
    public IReadOnlyList<MethodInfo> Methods { get; init; } = Array.Empty<MethodInfo>();
    public IReadOnlyList<PropertyInfo> Properties { get; init; } = Array.Empty<PropertyInfo>();
    public IReadOnlyList<FieldInfo> Fields { get; init; } = Array.Empty<FieldInfo>();
    public IReadOnlyList<EventInfo> Events { get; init; } = Array.Empty<EventInfo>();
    public IReadOnlyList<string> BaseTypes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Interfaces { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Lightweight type summary — only FullName, Namespace, Kind, Accessibility.
/// Used by ListTypes and AnalyzeAssembly to avoid full member mapping overhead.
/// </summary>
public sealed record TypeSummary
{
    public required string FullName { get; init; }
    public string? Namespace { get; init; }
    public required string ShortName { get; init; }
    public TypeKind Kind { get; init; }
    public Accessibility Accessibility { get; init; }
}

public enum TypeKind
{
    Class,
    Interface,
    Struct,
    Enum,
    Delegate,
    Unknown
}

public enum Accessibility
{
    Public,
    Internal,
    Protected,
    Private,
    ProtectedInternal,
    PrivateProtected
}

public sealed record MethodInfo
{
    public required string Name { get; init; }
    public required string ReturnType { get; init; }
    public IReadOnlyList<ParameterInfo> Parameters { get; init; } = Array.Empty<ParameterInfo>();
    public Accessibility Accessibility { get; init; }
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsExtensionMethod { get; init; }
}

public sealed record ParameterInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
}

public sealed record PropertyInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public Accessibility Accessibility { get; init; }
    public bool HasGetter { get; init; }
    public bool HasSetter { get; init; }
}

public sealed record FieldInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public Accessibility Accessibility { get; init; }
    public bool IsStatic { get; init; }
}

public sealed record EventInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public Accessibility Accessibility { get; init; }
}
