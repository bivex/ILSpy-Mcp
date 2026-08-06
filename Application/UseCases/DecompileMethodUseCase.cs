using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Application.UseCases;

public sealed class DecompileMethodUseCase
{
    private readonly IDecompilerService _decompiler;
    private readonly ITimeoutService _timeout;
    private readonly ILogger<DecompileMethodUseCase> _logger;

    public DecompileMethodUseCase(
        IDecompilerService decompiler,
        ITimeoutService timeout,
        ILogger<DecompileMethodUseCase> logger)
    {
        _decompiler = decompiler;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string assemblyPath,
        string typeName,
        string methodName,
        string? query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = AssemblyPath.Create(assemblyPath);
            var type = TypeName.Create(typeName);

            _logger.LogInformation("Decompiling method {MethodName} from {TypeName} in {Assembly}", 
                methodName, typeName, assemblyPath);

            using var timeoutCts = _timeout.CreateLinkedTimeout(cancellationToken);

            var methodCode = await _decompiler.DecompileMethodAsync(assembly, type, methodName, timeoutCts.Token);

            return methodCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Operation cancelled for method {MethodName}", methodName);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation timed out for method {MethodName}", methodName);
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
            _logger.LogWarning("Invalid argument for decompiling method {MethodName}: {Message}", methodName, ex.Message);
            throw new AssemblyLoadException(assemblyPath, ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error decompiling method {MethodName}", methodName);
            throw;
        }
    }
}
