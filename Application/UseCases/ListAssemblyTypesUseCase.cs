using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Application.UseCases;

public sealed class ListAssemblyTypesUseCase
{
    private readonly IDecompilerService _decompiler;
    private readonly ITimeoutService _timeout;
    private readonly ILogger<ListAssemblyTypesUseCase> _logger;

    public ListAssemblyTypesUseCase(
        IDecompilerService decompiler,
        ITimeoutService timeout,
        ILogger<ListAssemblyTypesUseCase> logger)
    {
        _decompiler = decompiler;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string assemblyPath,
        string? namespaceFilter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = AssemblyPath.Create(assemblyPath);

            _logger.LogInformation("Listing types from {Assembly} with filter: {Filter}", 
                assemblyPath, namespaceFilter ?? "none");

            using var timeoutCts = _timeout.CreateLinkedTimeout(cancellationToken);

            var types = await _decompiler.ListTypesAsync(assembly, namespaceFilter, timeoutCts.Token);

            var result = new System.Text.StringBuilder();
            result.AppendLine($"Assembly: {assembly.FileName}");
            result.AppendLine($"Types found: {types.Count}");
            result.AppendLine();

            foreach (var type in types)
            {
                var kind = type.Kind switch
                {
                    Domain.Models.TypeKind.Class     => "class",
                    Domain.Models.TypeKind.Interface => "interface",
                    Domain.Models.TypeKind.Struct    => "struct",
                    Domain.Models.TypeKind.Enum      => "enum",
                    Domain.Models.TypeKind.Delegate  => "delegate",
                    _                                => "unknown"
                };
                result.AppendLine($"  {kind,-10} {type.FullName}");
            }

            return result.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Operation cancelled for listing types from {Assembly}", assemblyPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation timed out for listing types from {Assembly}", assemblyPath);
            throw new TimeoutException($"Operation timed out after {_timeout.GetDefaultTimeout().TotalSeconds} seconds");
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error listing types from {Assembly}", assemblyPath);
            throw;
        }
    }
}
