using System.Collections.Concurrent;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Infrastructure.Decompiler;

/// <summary>
/// Adapter that wraps ILSpy decompiler to implement IDecompilerService.
/// </summary>
public sealed class ILSpyDecompilerService : IDecompilerService
{
    private static readonly ConcurrentDictionary<string, (DateTime LastWriteTime, CSharpDecompiler Decompiler, object SyncLock)> _decompilerCache = new();
    private readonly ILogger<ILSpyDecompilerService> _logger;
    private readonly DecompilerSettings _settings;

    public ILSpyDecompilerService(ILogger<ILSpyDecompilerService> logger)
    {
        _logger = logger;
        _settings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = false
        };
    }

    private (CSharpDecompiler Decompiler, object SyncLock) GetDecompiler(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var lastWriteTime = File.GetLastWriteTimeUtc(fullPath);

        if (_decompilerCache.TryGetValue(fullPath, out var cached) && cached.LastWriteTime == lastWriteTime)
        {
            return (cached.Decompiler, cached.SyncLock);
        }

        var decompiler = new CSharpDecompiler(fullPath, _settings);
        var lockObj = new object();
        _decompilerCache[fullPath] = (lastWriteTime, decompiler, lockObj);
        return (decompiler, lockObj);
    }

    public async Task<DecompilationResult> DecompileTypeAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var (decompiler, lockObj) = GetDecompiler(assemblyPath.Value);
                lock (lockObj)
                {
                    var type = decompiler.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(typeName.FullName));
                    
                    if (type == null)
                        throw new TypeNotFoundException(typeName.FullName, assemblyPath.Value);

                    var code = decompiler.DecompileTypeAsString(type.FullTypeName);
                    return new DecompilationResult(code, typeName.FullName, assemblyPath.FileName);
                }
            }
            catch (TypeNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompile type {TypeName} from {Assembly}", typeName.FullName, assemblyPath.Value);
                throw new AssemblyLoadException(assemblyPath.Value, ex);
            }
        }, cancellationToken);
    }

    public async Task<string> DecompileMethodAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var (decompiler, lockObj) = GetDecompiler(assemblyPath.Value);
                lock (lockObj)
                {
                    var type = decompiler.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(typeName.FullName));
                    
                    if (type == null)
                        throw new TypeNotFoundException(typeName.FullName, assemblyPath.Value);

                    var methods = type.Methods.Where(m => m.Name == methodName).ToList();
                    if (!methods.Any())
                        throw new MethodNotFoundException(methodName, typeName.FullName);

                    var codeBuilder = new System.Text.StringBuilder();
                    foreach (var method in methods)
                    {
                        var code = decompiler.DecompileAsString(method.MetadataToken);
                        codeBuilder.AppendLine($"// Overload with {method.Parameters.Count} parameter(s)");
                        codeBuilder.AppendLine(code);
                        codeBuilder.AppendLine();
                    }

                    return codeBuilder.ToString();
                }
            }
            catch (TypeNotFoundException)
            {
                throw;
            }
            catch (MethodNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decompile method {MethodName} from {TypeName} in {Assembly}", 
                    methodName, typeName.FullName, assemblyPath.Value);
                throw new AssemblyLoadException(assemblyPath.Value, ex);
            }
        }, cancellationToken);
    }

    public async Task<TypeInfo> GetTypeInfoAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var (decompiler, lockObj) = GetDecompiler(assemblyPath.Value);
                lock (lockObj)
                {
                    var type = decompiler.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(typeName.FullName));
                    
                    if (type == null)
                        throw new TypeNotFoundException(typeName.FullName, assemblyPath.Value);

                    return MapToTypeInfo(type);
                }
            }
            catch (TypeNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get type info for {TypeName} from {Assembly}", 
                    typeName.FullName, assemblyPath.Value);
                throw new AssemblyLoadException(assemblyPath.Value, ex);
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<TypeInfo>> ListTypesAsync(
        AssemblyPath assemblyPath,
        string? namespaceFilter = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var (decompiler, lockObj) = GetDecompiler(assemblyPath.Value);
                lock (lockObj)
                {
                    var mainModule = decompiler.TypeSystem.MainModule;
                    var types = mainModule.TypeDefinitions
                        .Where(t => 
                            // Only include types actually defined in this assembly (not type forwards)
                            t.ParentModule == mainModule &&
                            (string.IsNullOrEmpty(namespaceFilter) || 
                             (t.Namespace?.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase) ?? false)))
                        .Select(MapToTypeInfo)
                        .OrderBy(t => t.FullName)
                        .ToList();

                    return types;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list types from {Assembly}", assemblyPath.Value);
                throw new AssemblyLoadException(assemblyPath.Value, ex);
            }
        }, cancellationToken);
    }

    public async Task<AssemblyInfo> GetAssemblyInfoAsync(
        AssemblyPath assemblyPath,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var (decompiler, lockObj) = GetDecompiler(assemblyPath.Value);
                lock (lockObj)
                {
                    var mainModule = decompiler.TypeSystem.MainModule;
                    var publicTypes = mainModule.TypeDefinitions
                        .Where(t => 
                            // Only include types actually defined in this assembly (not type forwards)
                            t.ParentModule == mainModule &&
                            t.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public)
                        .Select(MapToTypeInfo)
                        .Take(100)
                        .ToList();

                    var namespaceCounts = publicTypes
                        .GroupBy(t => t.Namespace ?? "(global)")
                        .ToDictionary(g => g.Key, g => g.Count());

                    return new AssemblyInfo
                    {
                        FileName = assemblyPath.FileName,
                        FullPath = assemblyPath.Value,
                        PublicTypes = publicTypes,
                        NamespaceCounts = namespaceCounts,
                        TotalTypeCount = decompiler.TypeSystem.MainModule.TypeDefinitions.Count()
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get assembly info for {Assembly}", assemblyPath.Value);
                throw new AssemblyLoadException(assemblyPath.Value, ex);
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<MethodInfo>> FindExtensionMethodsAsync(
        AssemblyPath assemblyPath,
        TypeName targetType,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var (decompiler, lockObj) = GetDecompiler(assemblyPath.Value);
                lock (lockObj)
                {
                    var mainModule = decompiler.TypeSystem.MainModule;
                    var extensionMethods = new List<MethodInfo>();

                    foreach (var type in mainModule.TypeDefinitions
                        .Where(t => 
                            // Only include types actually defined in this assembly
                            t.ParentModule == mainModule &&
                            t.IsStatic && 
                            t.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public))
                    {
                        foreach (var method in type.Methods.Where(m => m.IsExtensionMethod))
                        {
                            if (method.Parameters.Count > 0)
                            {
                                var firstParam = method.Parameters[0];
                                var extendsType = firstParam.Type.FullName;
                                
                                if (extendsType.Equals(targetType.FullName, StringComparison.OrdinalIgnoreCase) ||
                                    targetType.FullName.Contains(extendsType, StringComparison.OrdinalIgnoreCase))
                                {
                                    extensionMethods.Add(MapToMethodInfo(method));
                                }
                            }
                        }
                    }

                    return extensionMethods;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to find extension methods for {TypeName} in {Assembly}", 
                    targetType.FullName, assemblyPath.Value);
                throw new AssemblyLoadException(assemblyPath.Value, ex);
            }
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<MemberSearchResult>> SearchMembersAsync(
        AssemblyPath assemblyPath,
        string searchTerm,
        string? memberKind = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var (decompiler, lockObj) = GetDecompiler(assemblyPath.Value);
                lock (lockObj)
                {
                    var mainModule = decompiler.TypeSystem.MainModule;
                    var results = new List<MemberSearchResult>();

                    foreach (var type in mainModule.TypeDefinitions
                        .Where(t => 
                            // Only include types actually defined in this assembly
                            t.ParentModule == mainModule &&
                            t.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public))
                    {
                        if (string.IsNullOrEmpty(memberKind) || memberKind.Equals("method", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var method in type.Methods
                                .Where(m => m.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) && !m.IsConstructor))
                            {
                                var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.Name} {p.Name}"));
                                results.Add(new MemberSearchResult
                                {
                                    TypeFullName = type.FullName,
                                    MemberName = method.Name,
                                    Kind = MemberKind.Method,
                                    Signature = $"{method.ReturnType.Name} {method.Name}({parameters})"
                                });
                            }
                        }

                        if (string.IsNullOrEmpty(memberKind) || memberKind.Equals("property", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var prop in type.Properties
                                .Where(p => p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                            {
                                results.Add(new MemberSearchResult
                                {
                                    TypeFullName = type.FullName,
                                    MemberName = prop.Name,
                                    Kind = MemberKind.Property,
                                    Signature = $"{prop.ReturnType.Name} {prop.Name}"
                                });
                            }
                        }

                        if (string.IsNullOrEmpty(memberKind) || memberKind.Equals("field", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var field in type.Fields
                                .Where(f => f.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                            {
                                results.Add(new MemberSearchResult
                                {
                                    TypeFullName = type.FullName,
                                    MemberName = field.Name,
                                    Kind = MemberKind.Field,
                                    Signature = $"{field.Type.Name} {field.Name}"
                                });
                            }
                        }

                        if (string.IsNullOrEmpty(memberKind) || memberKind.Equals("event", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var evt in type.Events
                                .Where(e => e.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                            {
                                results.Add(new MemberSearchResult
                                {
                                    TypeFullName = type.FullName,
                                    MemberName = evt.Name,
                                    Kind = MemberKind.Event,
                                    Signature = $"event {evt.ReturnType.Name} {evt.Name}"
                                });
                            }
                        }
                    }

                    return results;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search members in {Assembly}", assemblyPath.Value);
                throw new AssemblyLoadException(assemblyPath.Value, ex);
            }
        }, cancellationToken);
    }

    private static TypeInfo MapToTypeInfo(ITypeDefinition type)
    {
        return new TypeInfo
        {
            FullName = type.FullName,
            Namespace = type.Namespace,
            ShortName = type.Name,
            Kind = MapTypeKind(type.Kind),
            Accessibility = MapAccessibility(type.Accessibility),
            Methods = type.Methods.Where(m => !m.IsConstructor).Select(MapToMethodInfo).ToList(),
            Properties = type.Properties.Select(MapToPropertyInfo).ToList(),
            Fields = type.Fields.Select(MapToFieldInfo).ToList(),
            Events = type.Events.Select(MapToEventInfo).ToList(),
            BaseTypes = type.DirectBaseTypes
                .Where(t => t.Kind == ICSharpCode.Decompiler.TypeSystem.TypeKind.Class && t.FullName != "System.Object")
                .Select(t => t.FullName)
                .ToList(),
            Interfaces = type.DirectBaseTypes
                .Where(t => t.Kind == ICSharpCode.Decompiler.TypeSystem.TypeKind.Interface)
                .Select(t => t.FullName)
                .ToList()
        };
    }

    private static MethodInfo MapToMethodInfo(IMethod method)
    {
        return new MethodInfo
        {
            Name = method.Name,
            ReturnType = method.ReturnType.Name,
            Parameters = method.Parameters.Select(p => new ParameterInfo
            {
                Name = p.Name,
                Type = p.Type.Name
            }).ToList(),
            Accessibility = MapAccessibility(method.Accessibility),
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsExtensionMethod = method.IsExtensionMethod
        };
    }

    private static PropertyInfo MapToPropertyInfo(IProperty property)
    {
        return new PropertyInfo
        {
            Name = property.Name,
            Type = property.ReturnType.Name,
            Accessibility = MapAccessibility(property.Accessibility),
            HasGetter = property.Getter != null,
            HasSetter = property.Setter != null
        };
    }

    private static FieldInfo MapToFieldInfo(IField field)
    {
        return new FieldInfo
        {
            Name = field.Name,
            Type = field.Type.Name,
            Accessibility = MapAccessibility(field.Accessibility),
            IsStatic = field.IsStatic
        };
    }

    private static EventInfo MapToEventInfo(IEvent evt)
    {
        return new EventInfo
        {
            Name = evt.Name,
            Type = evt.ReturnType.Name,
            Accessibility = MapAccessibility(evt.Accessibility)
        };
    }

    private static Domain.Models.TypeKind MapTypeKind(ICSharpCode.Decompiler.TypeSystem.TypeKind kind) => kind switch
    {
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Class => Domain.Models.TypeKind.Class,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Interface => Domain.Models.TypeKind.Interface,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Struct => Domain.Models.TypeKind.Struct,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Enum => Domain.Models.TypeKind.Enum,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Delegate => Domain.Models.TypeKind.Delegate,
        _ => Domain.Models.TypeKind.Unknown
    };

    private static Domain.Models.Accessibility MapAccessibility(ICSharpCode.Decompiler.TypeSystem.Accessibility accessibility) => accessibility switch
    {
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Public => Domain.Models.Accessibility.Public,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Internal => Domain.Models.Accessibility.Internal,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Protected => Domain.Models.Accessibility.Protected,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Private => Domain.Models.Accessibility.Private,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.ProtectedOrInternal => Domain.Models.Accessibility.ProtectedInternal,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.ProtectedAndInternal => Domain.Models.Accessibility.PrivateProtected,
        _ => Domain.Models.Accessibility.Private
    };
}
