using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Application.UseCases;

public sealed class GetTypeMembersUseCase
{
    private readonly IDecompilerService _decompiler;
    private readonly ITimeoutService _timeout;
    private readonly ILogger<GetTypeMembersUseCase> _logger;

    public GetTypeMembersUseCase(
        IDecompilerService decompiler,
        ITimeoutService timeout,
        ILogger<GetTypeMembersUseCase> logger)
    {
        _decompiler = decompiler;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        string assemblyPath,
        string typeName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assembly = AssemblyPath.Create(assemblyPath);
            var type = TypeName.Create(typeName);

            _logger.LogInformation("Getting members for type {TypeName} from {Assembly}", typeName, assemblyPath);

            using var timeoutCts = _timeout.CreateLinkedTimeout(cancellationToken);

            var typeInfo = await _decompiler.GetTypeInfoAsync(assembly, type, timeoutCts.Token);

            var result = new System.Text.StringBuilder();
            result.AppendLine($"╔═══ Type Members: {typeInfo.FullName}");
            result.AppendLine($"║ Assembly: {assembly.FileName}");
            result.AppendLine($"║ Kind: {typeInfo.Kind}");
            result.AppendLine($"║ Namespace: {typeInfo.Namespace ?? "(global)"}");
            result.AppendLine($"╚═══");
            result.AppendLine();

            if (typeInfo.Methods.Count > 0)
            {
                result.AppendLine("Methods:");
                foreach (var method in typeInfo.Methods)
                {
                    var accessibility = AccessibilityLabel(method.Accessibility);
                    var mods = method.IsStatic && method.IsAbstract ? "static abstract "
                        : method.IsStatic ? "static "
                        : method.IsAbstract ? "abstract "
                        : method.IsVirtual ? "virtual "
                        : "";
                    var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));
                    result.AppendLine($"  {accessibility} {mods}{method.ReturnType} {method.Name}({parameters})");
                }
                result.AppendLine();
            }

            if (typeInfo.Properties.Count > 0)
            {
                result.AppendLine("Properties:");
                foreach (var prop in typeInfo.Properties)
                {
                    var accessibility = AccessibilityLabel(prop.Accessibility);
                    var getter = prop.HasGetter ? "get;" : "";
                    var setter = prop.HasSetter ? "set;" : "";
                    result.AppendLine($"  {accessibility} {prop.Type} {prop.Name} {{ {getter} {setter} }}");
                }
                result.AppendLine();
            }

            if (typeInfo.Fields.Count > 0)
            {
                result.AppendLine("Fields:");
                foreach (var field in typeInfo.Fields)
                {
                    var accessibility = AccessibilityLabel(field.Accessibility);
                    var modifiers = field.IsStatic ? "static " : "";
                    result.AppendLine($"  {accessibility} {modifiers}{field.Type} {field.Name}");
                }
                result.AppendLine();
            }

            if (typeInfo.Events.Count > 0)
            {
                result.AppendLine("Events:");
                foreach (var evt in typeInfo.Events)
                {
                    var accessibility = AccessibilityLabel(evt.Accessibility);
                    result.AppendLine($"  {accessibility} event {evt.Type} {evt.Name}");
                }
            }

            return result.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Operation cancelled for getting members of {TypeName}", typeName);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation timed out for getting members of {TypeName}", typeName);
            throw new TimeoutException($"Operation timed out after {_timeout.GetDefaultTimeout().TotalSeconds} seconds");
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error getting type members for {TypeName}", typeName);
            throw;
        }
    }

    private static string AccessibilityLabel(Domain.Models.Accessibility a) => a switch
    {
        Domain.Models.Accessibility.Public           => "public",
        Domain.Models.Accessibility.Internal         => "internal",
        Domain.Models.Accessibility.Protected        => "protected",
        Domain.Models.Accessibility.Private          => "private",
        Domain.Models.Accessibility.ProtectedInternal  => "protected internal",
        Domain.Models.Accessibility.PrivateProtected   => "private protected",
        _                                            => a.ToString().ToLower()
    };
}
