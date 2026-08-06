namespace ILSpy.Mcp.Domain.Models;

/// <summary>
/// High-level information about an assembly.
/// </summary>
public sealed record AssemblyInfo
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public IReadOnlyList<TypeSummary> PublicTypes { get; init; } = Array.Empty<TypeSummary>();
    public IReadOnlyDictionary<string, int> NamespaceCounts { get; init; } = new Dictionary<string, int>();
    public int TotalTypeCount { get; init; }
}
