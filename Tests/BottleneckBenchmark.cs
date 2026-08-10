using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly string _dllPath;
    private readonly IServiceProvider _sp;

    public BottleneckBenchmark(ITestOutputHelper output)
    {
        _out = output;
        var runtimePath = RuntimeEnvironment.GetRuntimeDirectory();
        var candidates = new[]
        {
            Path.Combine(runtimePath, "System.Text.Json.dll"),
            Path.Combine(runtimePath, "System.Private.CoreLib.dll")
        };
        _dllPath = candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException($"No test assembly found in {runtimePath}");

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

    [Fact(Skip = "Manual benchmark")]
    public async Task Bottleneck_FindTypeHierarchy_AllocatesFullTypeInfo()
    {
        _out.WriteLine("\n=== BOTTLENECK #1: FindTypeHierarchy over-fetches full TypeInfo ===");

        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<FindTypeHierarchyUseCase>()
                .ExecuteAsync(_dllPath, "System.String");

        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            using var scope = _sp.CreateScope();
            var uc = scope.ServiceProvider.GetRequiredService<FindTypeHierarchyUseCase>();
            var (ms, _) = await Measure($"run {i+1}", () => uc.ExecuteAsync(_dllPath, "System.String"));
            times.Add(ms);
        }
        _out.WriteLine($"  avg: {times.Average():F1} ms");
    }

    [Fact(Skip = "Manual benchmark")]
    public async Task Bottleneck_GetTypeMembers_FullMappingOnEveryCall()
    {
        _out.WriteLine("\n=== BOTTLENECK #2: GetTypeMembers maps all members on every call ===");

        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>()
                .ExecuteAsync(_dllPath, "System.String");

        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            using var scope = _sp.CreateScope();
            var uc = scope.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>();
            var (ms, _) = await Measure($"run {i+1}", () => uc.ExecuteAsync(_dllPath, "System.String"));
            times.Add(ms);
        }
        _out.WriteLine($"  avg: {times.Average():F1} ms");
    }

    [Fact(Skip = "Manual benchmark")]
    public async Task Bottleneck_ListTypes_MapsFullTypeInfoForSimpleListing()
    {
        _out.WriteLine("\n=== BOTTLENECK #3: ListTypes maps full TypeInfo just for FullName+Kind ===");

        using (var s = _sp.CreateScope())
        {
            var uc = s.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>();
            await Measure("CoreLib COLD", () => uc.ExecuteAsync(_dllPath, null));
        }
        using (var s = _sp.CreateScope())
        {
            var uc = s.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>();
            await Measure("CoreLib WARM", () => uc.ExecuteAsync(_dllPath, null));
        }
    }

    [Fact(Skip = "Manual benchmark")]
    public async Task Bottleneck_TimeoutService_NewCtsPerCall()
    {
        _out.WriteLine("\n=== BOTTLENECK #4: New CancellationTokenSource per use-case call ===");

        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>()
                .ExecuteAsync(_dllPath, null);

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
    }

    [Fact(Skip = "Manual benchmark")]
    public async Task Bottleneck_GetTypeMembers_ToStringToLowerInLoop()
    {
        _out.WriteLine("\n=== BOTTLENECK #5: .ToString().ToLower() on accessibility in hot loop ===");

        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>()
                .ExecuteAsync(_dllPath, "System.String");

        var sw = Stopwatch.StartNew();
        const int N = 20;
        for (int i = 0; i < N; i++)
        {
            using var scope = _sp.CreateScope();
            await scope.ServiceProvider.GetRequiredService<GetTypeMembersUseCase>()
                .ExecuteAsync(_dllPath, "System.String");
        }
        sw.Stop();
        _out.WriteLine($"  {N} calls: {sw.Elapsed.TotalMilliseconds:F1} ms total, avg {sw.Elapsed.TotalMilliseconds / N:F2} ms/call");
    }

    [Fact(Skip = "Manual benchmark")]
    public async Task Bottleneck_SearchMembers_FullScanNoIndex()
    {
        _out.WriteLine("\n=== BOTTLENECK #6: SearchMembers — full O(types × members) scan ===");

        using (var s = _sp.CreateScope())
            await s.ServiceProvider.GetRequiredService<SearchMembersByNameUseCase>()
                .ExecuteAsync(_dllPath, "ToString", null);

        var times = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            using var scope = _sp.CreateScope();
            var uc = scope.ServiceProvider.GetRequiredService<SearchMembersByNameUseCase>();
            var (ms, result) = await Measure($"run {i+1}", () => uc.ExecuteAsync(_dllPath, "ToString", null));
            times.Add(ms);
        }
        _out.WriteLine($"  avg: {times.Average():F1} ms");
    }
}
