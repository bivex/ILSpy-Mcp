using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;
using System.Text;

namespace ILSpy.Mcp.Application.UseCases;

public sealed class AnalyzeAssemblyUseCase
{
    private readonly IDecompilerService _decompiler;
    private readonly ITimeoutService _timeout;
    private readonly ILogger<AnalyzeAssemblyUseCase> _logger;

    public AnalyzeAssemblyUseCase(
        IDecompilerService decompiler,
        ITimeoutService timeout,
        ILogger<AnalyzeAssemblyUseCase> logger)
    {
        _decompiler = decompiler;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string assemblyPath,
        string? query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = AssemblyPath.Create(assemblyPath);

            _logger.LogInformation("Analyzing assembly {Assembly}", assemblyPath);

            using var timeoutCts = _timeout.CreateLinkedTimeout(cancellationToken);

            var assemblyInfo = await _decompiler.GetAssemblyInfoAsync(assembly, timeoutCts.Token);

            // Build a summary of the assembly
            var result = new StringBuilder();
            result.AppendLine($"Assembly: {assemblyInfo.FileName}");
            result.AppendLine($"Total Types: {assemblyInfo.TotalTypeCount}");
            result.AppendLine($"Public Types: {assemblyInfo.PublicTypes.Count}");
            result.AppendLine();

            if (assemblyInfo.NamespaceCounts.Count > 0)
            {
                result.AppendLine("Namespaces:");
                // TODO: NamespaceCounts is built in a single pass; consider pre-sorting at construction time
                foreach (var ns in assemblyInfo.NamespaceCounts.OrderByDescending(kvp => kvp.Value))
                {
                    result.AppendLine($"  {ns.Key}: {ns.Value} types");
                }
                result.AppendLine();
            }

            if (assemblyInfo.PublicTypes.Count > 0)
            {
                result.AppendLine("Key Public Types:");
                foreach (var type in assemblyInfo.PublicTypes)
                {
                    result.AppendLine($"  {type.Kind} {type.FullName}");
                }
            }

            return result.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Operation cancelled for assembly {Assembly}", assemblyPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation timed out for assembly {Assembly}", assemblyPath);
            throw new TimeoutException($"Operation timed out after {_timeout.GetDefaultTimeout().TotalSeconds} seconds");
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error analyzing assembly {Assembly}", assemblyPath);
            throw;
        }
    }
}
