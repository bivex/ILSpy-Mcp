using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using ILSpy.Mcp.Application.Configuration;
using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Application.UseCases;
using ILSpy.Mcp.Domain.Services;
using ILSpy.Mcp.Infrastructure.Decompiler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ILSpy.Mcp.Tests;

/// <summary>
/// Microbenchmarks to identify bottlenecks across the MCP stack.
/// </summary>
public class BottleneckBenchmark
{
    private readonly ITestOutputHelper _out;
    private readonly string _dllPath = "/usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.9/System.Text.Json.dll";
    private readonly IServiceProvider _sp;

    public BottleneckBenchmark(ITestOutputHelper output)
    {
        _out = output;
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.Configure<ILSpyOptions>(o =>
        {
            o.DefaultTimeoutSeconds = 60;
            o.MaxDecompilationSize = 5_000_000;
            o.MaxConcurrentOperations = 10;
        });
        services.AddSingleton<ITimeoutService, TimeoutService>();
        services.AddScoped<IDecompilerService, ILSpyDecompilerService>();
        services.AddScoped<ListAssemblyTypesUseCase>();
        services.AddScoped<DecompileTypeUseCase>();
        services.AddScoped<SearchMembersByNameUseCase>();
        services.AddScoped<AnalyzeAssemblyUseCase>();
        services.AddScoped<GetTypeMembersUseCase>();
        services.AddScoped<FindTypeHierarchyUseCase>();
        _sp = services.BuildServiceProvider();
    }

    private async Task<(double ms, T result)> Measure<T>(string label, Func<Task<T>> fn)
    {
        var sw = Stopwatch.StartNew();
        var result = await fn();
        sw.Stop();
        _out.WriteLine($"  [{label}] {sw.Elapsed.TotalMilliseconds:F1} ms");
        return (sw.Elapsed.TotalMilliseconds, result);
    }

    /// <summary>
    /// BOTTLENECK #1: GetTypeInfo allocates full TypeInfo (with all members) even when
    /// FindTypeHierarchy only needs BaseTypes + Interfaces (2 fields out of ~8).
    /// </summary>
    [Fact]
    public async Task Bottleneck_FindTypeHierarchy_AllocatesFullTypeInfo()
    {
        _out.WriteLine("\n=== BOTTLENECK #1: FindTypeHierarchy over-fetches full TypeInfo ===");

        // Warm cache
        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<FindTypeHierarchyUseCase>()
                .ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer");

        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            using var scope = _sp.CreateScope();
            var uc = scope.ServiceProvider.GetRequiredService<FindTypeHierarchyUseCase>();
            var (ms, _) = await Measure($"run {i+1}", () => uc.ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer"));
            times.Add(ms);
        }
        _out.WriteLine($"  avg: {times.Average():F1} ms — FULL TypeInfo (methods, props, fields, events) wasted for hierarchy-only query");
    }

    /// <summary>
    /// BOTTLENECK #2: GetTypeMembersUseCase calls GetTypeInfoAsync which maps ALL members
    /// (methods, properties, fields, events) into domain objects via MapToTypeInfo —
    /// heavy LINQ allocation even for types with hundreds of members.
    /// </summary>
    [Fact]
    public async Task Bottleneck_GetTypeMembers_FullMappingOnEveryCall()
    {
        _out.WriteLine("\n=== BOTTLENECK #2: GetTypeMembers maps all members on every call ===");

        // Warm cache
        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>()
                .ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer");

        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            using var scope = _sp.CreateScope();
            var uc = scope.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>();
            var (ms, _) = await Measure($"run {i+1}", () => uc.ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer"));
            times.Add(ms);
        }
        _out.WriteLine($"  avg: {times.Average():F1} ms — MapToTypeInfo walks all 100+ methods even for simple member listing");
    }

    /// <summary>
    /// BOTTLENECK #3: ListTypesAsync maps ALL types to full TypeInfo (including all members)
    /// just to output "kind + FullName". For large assemblies this is extremely wasteful.
    /// System.Private.CoreLib has 2000+ types.
    /// </summary>
    [Fact]
    public async Task Bottleneck_ListTypes_MapsFullTypeInfoForSimpleListing()
    {
        _out.WriteLine("\n=== BOTTLENECK #3: ListTypes maps full TypeInfo just for FullName+Kind ===");

        var largerDll = "/usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.9/System.Private.CoreLib.dll";

        // Cold
        using (var s = _sp.CreateScope())
        {
            var uc = s.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>();
            await Measure("CoreLib COLD", () => uc.ExecuteAsync(largerDll, null));
        }
        // Warm
        using (var s = _sp.CreateScope())
        {
            var uc = s.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>();
            await Measure("CoreLib WARM", () => uc.ExecuteAsync(largerDll, null));
        }
        _out.WriteLine("  => MapToTypeInfo called for ALL 2000+ types even though only FullName+Kind are used in output");
    }

    /// <summary>
    /// BOTTLENECK #4: TimeoutService creates a new CancellationTokenSource on EVERY call,
    /// even for operations that complete in < 5ms. CTS allocation + timer is not free.
    /// </summary>
    [Fact]
    public async Task Bottleneck_TimeoutService_NewCtsPerCall()
    {
        _out.WriteLine("\n=== BOTTLENECK #4: New CancellationTokenSource per use-case call ===");

        // Warm
        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>()
                .ExecuteAsync(_dllPath, null);

        // Measure rapid repeated calls
        var sw = Stopwatch.StartNew();
        const int N = 50;
        for (int i = 0; i < N; i++)
        {
            using var scope = _sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>()
                .ExecuteAsync(_dllPath, null);
        }
        sw.Stop();
        _out.WriteLine($"  {N} calls: {sw.Elapsed.TotalMilliseconds:F1} ms total, avg {sw.Elapsed.TotalMilliseconds / N:F2} ms/call");
        _out.WriteLine($"  => Each call allocates 2× CTS (TimeoutService.CreateTimeoutToken + linked in use-case)");
    }

    /// <summary>
    /// BOTTLENECK #5: ToString().ToLower() in hot loops (GetTypeMembersUseCase).
    /// Called on every method/property/field for accessibility string formatting.
    /// </summary>
    [Fact]
    public async Task Bottleneck_GetTypeMembers_ToStringToLowerInLoop()
    {
        _out.WriteLine("\n=== BOTTLENECK #5: .ToString().ToLower() on accessibility in hot loop ===");

        // Warm
        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>()
                .ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer");

        var sw = Stopwatch.StartNew();
        const int N = 20;
        for (int i = 0; i < N; i++)
        {
            using var scope = _sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>()
                .ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer");
        }
        sw.Stop();
        _out.WriteLine($"  {N} calls: {sw.Elapsed.TotalMilliseconds:F1} ms total, avg {sw.Elapsed.TotalMilliseconds / N:F2} ms/call");
        _out.WriteLine($"  => accessibility.ToString().ToLower() + new List<string>() + modifiers.Any() on EVERY member in loop");
    }

    /// <summary>
    /// BOTTLENECK #6: SearchMembers does a full scan of ALL public types and ALL members
    /// even for very specific search terms. No early exit, no indexing.
    /// </summary>
    [Fact]
    public async Task Bottleneck_SearchMembers_FullScanNoIndex()
    {
        _out.WriteLine("\n=== BOTTLENECK #6: SearchMembers — full O(types × members) scan, no index ===");

        var largerDll = "/usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.9/System.Private.CoreLib.dll";

        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<SearchMembersByNameUseCase>()
                .ExecuteAsync(largerDll, "ToString", null);

        var times = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            using var scope = _sp.CreateScope();
            var uc = scope.ServiceProvider.GetRequiredService<SearchMembersByNameUseCase>();
            var (ms, result) = await Measure($"run {i+1}", () => uc.ExecuteAsync(largerDll, "ToString", null));
            times.Add(ms);
        }
        _out.WriteLine($"  avg: {times.Average():F1} ms — scans every method/property/field/event of every public type in CoreLib");
    }
}
