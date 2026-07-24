using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Application.UseCases;

/// <summary>
/// Use case for decompiling a type.
/// </summary>
public sealed class DecompileTypeUseCase
{
    private readonly IDecompilerService _decompiler;
    private readonly ITimeoutService _timeout;
    private readonly ILogger<DecompileTypeUseCase> _logger;

    public DecompileTypeUseCase(
        IDecompilerService decompiler,
        ITimeoutService timeout,
        ILogger<DecompileTypeUseCase> logger)
    {
        _decompiler = decompiler;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string assemblyPath,
        string typeName,
        string? query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = AssemblyPath.Create(assemblyPath);
            var type = TypeName.Create(typeName);

            _logger.LogInformation("Decompiling type {TypeName} from {Assembly}", typeName, assemblyPath);

            // Apply timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, 
                _timeout.CreateTimeoutToken());

            var decompilation = await _decompiler.DecompileTypeAsync(assembly, type, timeoutCts.Token);

            return decompilation.SourceCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Operation cancelled for type {TypeName}", typeName);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation timed out for type {TypeName}", typeName);
            throw new TimeoutException($"Operation timed out after {_timeout.GetDefaultTimeout().TotalSeconds} seconds");
        }
        catch (DomainException)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning("Assembly file not found: {Assembly}", assemblyPath);
            throw new AssemblyLoadException(assemblyPath, ex);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid argument for decompiling type {TypeName}: {Message}", typeName, ex.Message);
            throw new AssemblyLoadException(assemblyPath, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error decompiling type {TypeName}", typeName);
            throw;
        }
    }
}
