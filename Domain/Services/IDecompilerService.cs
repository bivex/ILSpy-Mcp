using ILSpy.Mcp.Domain.Models;

namespace ILSpy.Mcp.Domain.Services;

/// <summary>
/// Port for decompilation operations. Abstracts the decompiler implementation.
/// </summary>
public interface IDecompilerService
{
    Task<DecompilationResult> DecompileTypeAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        CancellationToken cancellationToken = default);

    Task<string> DecompileMethodAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        string methodName,
        CancellationToken cancellationToken = default);

    Task<TypeInfo> GetTypeInfoAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TypeSummary>> ListTypesAsync(
        AssemblyPath assemblyPath,
        string? namespaceFilter = null,
        CancellationToken cancellationToken = default);

    Task<AssemblyInfo> GetAssemblyInfoAsync(
        AssemblyPath assemblyPath,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MethodInfo>> FindExtensionMethodsAsync(
        AssemblyPath assemblyPath,
        TypeName targetType,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemberSearchResult>> SearchMembersAsync(
        AssemblyPath assemblyPath,
        string searchTerm,
        string? memberKind = null,
        CancellationToken cancellationToken = default);
}

public sealed record MemberSearchResult
{
    public required string TypeFullName { get; init; }
    public required string MemberName { get; init; }
    public MemberKind Kind { get; init; }
    public required string Signature { get; init; }
}

public enum MemberKind
{
    Method,
    Property,
    Field,
    Event
}
