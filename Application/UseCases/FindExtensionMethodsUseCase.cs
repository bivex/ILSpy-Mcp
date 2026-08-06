using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Application.UseCases;

public sealed class FindExtensionMethodsUseCase
{
    private readonly IDecompilerService _decompiler;
    private readonly ITimeoutService _timeout;
    private readonly ILogger<FindExtensionMethodsUseCase> _logger;

    public FindExtensionMethodsUseCase(
        IDecompilerService decompiler,
        ITimeoutService timeout,
        ILogger<FindExtensionMethodsUseCase> logger)
    {
        _decompiler = decompiler;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string assemblyPath,
        string targetTypeName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = AssemblyPath.Create(assemblyPath);
            var targetType = TypeName.Create(targetTypeName);

            _logger.LogInformation("Finding extension methods for {TypeName} in {Assembly}", 
                targetTypeName, assemblyPath);

            using var timeoutCts = _timeout.CreateLinkedTimeout(cancellationToken);

            var extensionMethods = await _decompiler.FindExtensionMethodsAsync(assembly, targetType, timeoutCts.Token);

            var result = new System.Text.StringBuilder();
            result.AppendLine($"Extension methods for type: {targetTypeName}");
            result.AppendLine($"Assembly: {assembly.FileName}");
            result.AppendLine();

            if (extensionMethods.Count > 0)
            {
                result.AppendLine($"Found {extensionMethods.Count} extension methods:");
                result.AppendLine();

                var grouped = extensionMethods.GroupBy(m => m.Name);
                foreach (var group in grouped)
                {
                    result.AppendLine($"Method: {group.Key}");
                    foreach (var method in group)
                    {
                        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));
                        result.AppendLine($"  {method.ReturnType} {method.Name}({parameters})");
                    }
                    result.AppendLine();
                }

                result.AppendLine("Usage: these methods can be called as if they were instance methods on the target type.");
            }
            else
            {
                result.AppendLine("No extension methods found for this type in the assembly.");
                result.AppendLine();
                result.AppendLine("Note: Extension methods are defined in static classes and marked with the 'this' keyword on their first parameter.");
            }

            return result.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Operation cancelled for finding extension methods for {TypeName}", targetTypeName);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation timed out for finding extension methods for {TypeName}", targetTypeName);
            throw new TimeoutException($"Operation timed out after {_timeout.GetDefaultTimeout().TotalSeconds} seconds");
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error finding extension methods for {TypeName}", targetTypeName);
            throw;
        }
    }
}
