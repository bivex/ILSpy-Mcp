using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Application.UseCases;

public sealed class SearchMembersByNameUseCase
{
    private readonly IDecompilerService _decompiler;
    private readonly ITimeoutService _timeout;
    private readonly ILogger<SearchMembersByNameUseCase> _logger;

    public SearchMembersByNameUseCase(
        IDecompilerService decompiler,
        ITimeoutService timeout,
        ILogger<SearchMembersByNameUseCase> logger)
    {
        _decompiler = decompiler;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string assemblyPath,
        string searchTerm,
        string? memberKind,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = AssemblyPath.Create(assemblyPath);

            _logger.LogInformation("Searching members in {Assembly} for '{SearchTerm}' (kind: {Kind})", 
                assemblyPath, searchTerm, memberKind ?? "any");

            using var timeoutCts = _timeout.CreateLinkedTimeout(cancellationToken);

            var results = await _decompiler.SearchMembersAsync(assembly, searchTerm, memberKind, timeoutCts.Token);

            var result = new System.Text.StringBuilder();
            result.AppendLine($"Search results for '{searchTerm}' in {assembly.FileName}");
            result.AppendLine();

            result.AppendLine($"Found {results.Count} matching members:");
            result.AppendLine();

            var grouped = results.GroupBy(m => m.TypeFullName);
            foreach (var group in grouped)
            {
                result.AppendLine($"In type: {group.Key}");
                foreach (var member in group)
                {
                    result.AppendLine($"  [{member.Kind}] {member.Signature}");
                }
                result.AppendLine();
            }

            return result.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Operation cancelled for searching members in {Assembly}", assemblyPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation timed out for searching members in {Assembly}", assemblyPath);
            throw new TimeoutException($"Operation timed out after {_timeout.GetDefaultTimeout().TotalSeconds} seconds");
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error searching members in {Assembly}", assemblyPath);
            throw;
        }
    }
}
