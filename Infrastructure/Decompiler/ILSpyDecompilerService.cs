using System.Collections.Concurrent;
using System.IO;
using System.Reflection.PortableExecutable;
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
/// Features a thread-safe, bounded LRU cache using non-exclusive file streams
/// (FileShare.ReadWrite | FileShare.Delete) and async SemaphoreSlim locks.
/// </summary>
public sealed class ILSpyDecompilerService : IDecompilerService
{
    private sealed class CacheEntry : IDisposable
    {
        public DateTime LastWriteTime { get; set; }
        public DateTime LastChecked { get; set; }
        public PEFile PEFile { get; }
        public CSharpDecompiler Decompiler { get; }
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public CacheEntry(DateTime lastWriteTime, DateTime lastChecked, PEFile peFile, CSharpDecompiler decompiler)
        {
            LastWriteTime = lastWriteTime;
            LastChecked = lastChecked;
            PEFile = peFile;
            Decompiler = decompiler;
        }

        public void Dispose()
        {
            Semaphore.Dispose();
            PEFile.Dispose();
        }
    }

    private static readonly int MaxCacheEntries = 16;
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);

    // Global lock protecting cache dictionary operations and LRU ordering
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> LruList = new();

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

    private static PEFile CreatePEFile(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new PEFile(fullPath, stream, PEStreamOptions.PrefetchEntireImage);
    }

    private static CSharpDecompiler CreateDecompiler(PEFile peFile, DecompilerSettings settings)
    {
        var resolver = new UniversalAssemblyResolver(peFile.FileName, false, peFile.DetectTargetFrameworkId());
        var typeSystem = new DecompilerTypeSystem(peFile, resolver, settings);
        return new CSharpDecompiler(typeSystem, settings);
    }

    private CacheEntry GetOrCreateCacheEntry(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var now = DateTime.UtcNow;

        lock (CacheLock)
        {
            if (Cache.TryGetValue(fullPath, out var entry))
            {
                // Touch LRU
                LruList.Remove(fullPath);
                LruList.AddFirst(fullPath);

                if (now - entry.LastChecked < RecheckInterval)
                {
                    return entry;
                }

                var currentWriteTime = File.GetLastWriteTimeUtc(fullPath);
                if (currentWriteTime == entry.LastWriteTime)
                {
                    entry.LastChecked = now;
                    return entry;
                }

                // File has changed — remove old entry (will dispose below)
                Cache.Remove(fullPath);
                LruList.Remove(fullPath);
                entry.Dispose();
            }

            // Create new entry
            var writeTime = File.GetLastWriteTimeUtc(fullPath);
            var peFile = CreatePEFile(fullPath);
            var decompiler = CreateDecompiler(peFile, _settings);
            var newEntry = new CacheEntry(writeTime, now, peFile, decompiler);

            // Evict LRU if full
            while (Cache.Count >= MaxCacheEntries && LruList.Last != null)
            {
                var oldestKey = LruList.Last.Value;
                LruList.RemoveLast();
                if (Cache.Remove(oldestKey, out var evicted))
                {
                    evicted.Dispose();
                }
            }

            Cache[fullPath] = newEntry;
            LruList.AddFirst(fullPath);
            return newEntry;
        }
    }

    public async Task<DecompilationResult> DecompileTypeAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateCacheEntry(assemblyPath.Value);
        await entry.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var type = entry.Decompiler.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(typeName.FullName));
            if (type == null)
                throw new TypeNotFoundException(typeName.FullName, assemblyPath.Value);

            var code = entry.Decompiler.DecompileTypeAsString(type.FullTypeName);
            return new DecompilationResult(code, typeName.FullName, assemblyPath.FileName);
        }
        catch (TypeNotFoundException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decompile type {TypeName} from {Assembly}", typeName.FullName, assemblyPath.Value);
            throw new AssemblyLoadException(assemblyPath.Value, ex);
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    public async Task<string> DecompileMethodAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        string methodName,
        CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateCacheEntry(assemblyPath.Value);
        await entry.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var type = entry.Decompiler.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(typeName.FullName));
            if (type == null)
                throw new TypeNotFoundException(typeName.FullName, assemblyPath.Value);

            var methods = type.Methods.Where(m => m.Name == methodName).ToList();
            if (!methods.Any())
                throw new MethodNotFoundException(methodName, typeName.FullName);

            var codeBuilder = new System.Text.StringBuilder();
            foreach (var method in methods)
            {
                var code = entry.Decompiler.DecompileAsString(method.MetadataToken);
                codeBuilder.AppendLine($"// Overload with {method.Parameters.Count} parameter(s)");
                codeBuilder.AppendLine(code);
                codeBuilder.AppendLine();
            }

            return codeBuilder.ToString();
        }
        catch (TypeNotFoundException) { throw; }
        catch (MethodNotFoundException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decompile method {MethodName} from {TypeName} in {Assembly}",
                methodName, typeName.FullName, assemblyPath.Value);
            throw new AssemblyLoadException(assemblyPath.Value, ex);
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    public async Task<TypeInfo> GetTypeInfoAsync(
        AssemblyPath assemblyPath,
        TypeName typeName,
        CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateCacheEntry(assemblyPath.Value);
        await entry.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var type = entry.Decompiler.TypeSystem.MainModule.GetTypeDefinition(new FullTypeName(typeName.FullName));
            if (type == null)
                throw new TypeNotFoundException(typeName.FullName, assemblyPath.Value);

            return MapToTypeInfo(type);
        }
        catch (TypeNotFoundException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get type info for {TypeName} from {Assembly}",
                typeName.FullName, assemblyPath.Value);
            throw new AssemblyLoadException(assemblyPath.Value, ex);
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<TypeSummary>> ListTypesAsync(
        AssemblyPath assemblyPath,
        string? namespaceFilter = null,
        CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateCacheEntry(assemblyPath.Value);
        await entry.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var mainModule = entry.Decompiler.TypeSystem.MainModule;
            var types = mainModule.TypeDefinitions
                .Where(t =>
                    t.ParentModule == mainModule &&
                    (string.IsNullOrEmpty(namespaceFilter) ||
                     (t.Namespace?.Contains(namespaceFilter, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Select(MapToTypeSummary)
                .OrderBy(t => t.FullName)
                .ToList();

            return types;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list types from {Assembly}", assemblyPath.Value);
            throw new AssemblyLoadException(assemblyPath.Value, ex);
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    public async Task<AssemblyInfo> GetAssemblyInfoAsync(
        AssemblyPath assemblyPath,
        CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateCacheEntry(assemblyPath.Value);
        await entry.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var mainModule = entry.Decompiler.TypeSystem.MainModule;

            int totalCount = 0;
            var publicTypes = new List<TypeSummary>(128);
            var namespaceCounts = new Dictionary<string, int>(64);

            foreach (var t in mainModule.TypeDefinitions)
            {
                if (t.ParentModule != mainModule) continue;
                totalCount++;

                if (t.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public)
                {
                    if (publicTypes.Count < 100)
                        publicTypes.Add(MapToTypeSummary(t));

                    var ns = t.Namespace ?? "(global)";
                    namespaceCounts[ns] = namespaceCounts.TryGetValue(ns, out var c) ? c + 1 : 1;
                }
            }

            return new AssemblyInfo
            {
                FileName = assemblyPath.FileName,
                FullPath = assemblyPath.Value,
                PublicTypes = publicTypes,
                NamespaceCounts = namespaceCounts,
                TotalTypeCount = totalCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get assembly info for {Assembly}", assemblyPath.Value);
            throw new AssemblyLoadException(assemblyPath.Value, ex);
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<MethodInfo>> FindExtensionMethodsAsync(
        AssemblyPath assemblyPath,
        TypeName targetType,
        CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateCacheEntry(assemblyPath.Value);
        await entry.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var mainModule = entry.Decompiler.TypeSystem.MainModule;
            var extensionMethods = new List<MethodInfo>();

            foreach (var type in mainModule.TypeDefinitions
                .Where(t =>
                    t.ParentModule == mainModule &&
                    t.IsStatic &&
                    t.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public))
            {
                foreach (var method in type.Methods.Where(m => m.IsExtensionMethod))
                {
                    if (method.Parameters.Count > 0)
                    {
                        var extendsType = method.Parameters[0].Type.FullName;

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find extension methods for {TypeName} in {Assembly}",
                targetType.FullName, assemblyPath.Value);
            throw new AssemblyLoadException(assemblyPath.Value, ex);
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<MemberSearchResult>> SearchMembersAsync(
        AssemblyPath assemblyPath,
        string searchTerm,
        string? memberKind = null,
        CancellationToken cancellationToken = default)
    {
        var entry = GetOrCreateCacheEntry(assemblyPath.Value);
        await entry.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var mainModule = entry.Decompiler.TypeSystem.MainModule;
            var results = new List<MemberSearchResult>(64);

            bool searchAll = string.IsNullOrEmpty(memberKind);
            bool searchMethods    = searchAll || memberKind!.Equals("method",   StringComparison.OrdinalIgnoreCase);
            bool searchProperties = searchAll || memberKind!.Equals("property", StringComparison.OrdinalIgnoreCase);
            bool searchFields     = searchAll || memberKind!.Equals("field",    StringComparison.OrdinalIgnoreCase);
            bool searchEvents     = searchAll || memberKind!.Equals("event",    StringComparison.OrdinalIgnoreCase);

            const int MaxResults = 200;

            foreach (var type in mainModule.TypeDefinitions
                .Where(t =>
                    t.ParentModule == mainModule &&
                    t.Accessibility == ICSharpCode.Decompiler.TypeSystem.Accessibility.Public))
            {
                if (results.Count >= MaxResults) break;

                if (searchMethods)
                {
                    foreach (var method in type.Methods
                        .Where(m => !m.IsConstructor && m.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (results.Count >= MaxResults) break;
                        results.Add(new MemberSearchResult
                        {
                            TypeFullName = type.FullName,
                            MemberName = method.Name,
                            Kind = MemberKind.Method,
                            Signature = BuildMethodSignature(method)
                        });
                    }
                }

                if (searchProperties)
                {
                    foreach (var prop in type.Properties
                        .Where(p => p.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (results.Count >= MaxResults) break;
                        results.Add(new MemberSearchResult
                        {
                            TypeFullName = type.FullName,
                            MemberName = prop.Name,
                            Kind = MemberKind.Property,
                            Signature = $"{prop.ReturnType.Name} {prop.Name}"
                        });
                    }
                }

                if (searchFields)
                {
                    foreach (var field in type.Fields
                        .Where(f => f.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (results.Count >= MaxResults) break;
                        results.Add(new MemberSearchResult
                        {
                            TypeFullName = type.FullName,
                            MemberName = field.Name,
                            Kind = MemberKind.Field,
                            Signature = $"{field.Type.Name} {field.Name}"
                        });
                    }
                }

                if (searchEvents)
                {
                    foreach (var evt in type.Events
                        .Where(e => e.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (results.Count >= MaxResults) break;
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search members in {Assembly}", assemblyPath.Value);
            throw new AssemblyLoadException(assemblyPath.Value, ex);
        }
        finally
        {
            entry.Semaphore.Release();
        }
    }

    private static string BuildMethodSignature(IMethod method)
    {
        if (method.Parameters.Count == 0)
            return $"{method.ReturnType.Name} {method.Name}()";

        var sb = new System.Text.StringBuilder();
        sb.Append(method.ReturnType.Name);
        sb.Append(' ');
        sb.Append(method.Name);
        sb.Append('(');
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(method.Parameters[i].Type.Name);
            sb.Append(' ');
            sb.Append(method.Parameters[i].Name);
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static TypeSummary MapToTypeSummary(ITypeDefinition type) => new()
    {
        FullName = type.FullName,
        Namespace = type.Namespace,
        ShortName = type.Name,
        Kind = MapTypeKind(type.Kind),
        Accessibility = MapAccessibility(type.Accessibility)
    };

    private static TypeInfo MapToTypeInfo(ITypeDefinition type)
    {
        var baseTypes = new List<string>();
        var interfaces = new List<string>();

        foreach (var baseType in type.DirectBaseTypes)
        {
            if (baseType.Kind == ICSharpCode.Decompiler.TypeSystem.TypeKind.Class && baseType.FullName != "System.Object")
                baseTypes.Add(baseType.FullName);
            else if (baseType.Kind == ICSharpCode.Decompiler.TypeSystem.TypeKind.Interface)
                interfaces.Add(baseType.FullName);
        }

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
            BaseTypes = baseTypes,
            Interfaces = interfaces
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
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Class     => Domain.Models.TypeKind.Class,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Interface => Domain.Models.TypeKind.Interface,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Struct    => Domain.Models.TypeKind.Struct,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Enum      => Domain.Models.TypeKind.Enum,
        ICSharpCode.Decompiler.TypeSystem.TypeKind.Delegate  => Domain.Models.TypeKind.Delegate,
        _ => Domain.Models.TypeKind.Unknown
    };

    private static Domain.Models.Accessibility MapAccessibility(ICSharpCode.Decompiler.TypeSystem.Accessibility accessibility) => accessibility switch
    {
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Public              => Domain.Models.Accessibility.Public,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Internal            => Domain.Models.Accessibility.Internal,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Protected           => Domain.Models.Accessibility.Protected,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.Private             => Domain.Models.Accessibility.Private,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.ProtectedOrInternal => Domain.Models.Accessibility.ProtectedInternal,
        ICSharpCode.Decompiler.TypeSystem.Accessibility.ProtectedAndInternal => Domain.Models.Accessibility.PrivateProtected,
        _ => Domain.Models.Accessibility.Private
    };
}
