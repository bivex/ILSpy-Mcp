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

public class DecompilerCacheBenchmark
{
    private readonly ITestOutputHelper _out;
    private readonly string _dllPath = "/usr/local/share/dotnet/shared/Microsoft.NETCore.App/10.0.9/System.Text.Json.dll";
    private readonly IServiceProvider _sp;

    public DecompilerCacheBenchmark(ITestOutputHelper output)
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
        _sp = services.BuildServiceProvider();
    }

    private async Task<double> MeasureMs(string label, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        await action();
        sw.Stop();
        _out.WriteLine($"  [{label}] {sw.Elapsed.TotalMilliseconds:F1} ms");
        return sw.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public async Task Benchmark_ListTypes_ColdVsWarm()
    {
        _out.WriteLine($"\n=== ListAssemblyTypes: {Path.GetFileName(_dllPath)} ===");

        double cold, warm;
        using (var scope = _sp.CreateScope())
        {
            var uc = scope.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>();
            cold = await MeasureMs("COLD (1st call)", () => uc.ExecuteAsync(_dllPath, null));
        }
        using (var scope = _sp.CreateScope())
        {
            var uc = scope.ServiceProvider.GetRequiredService<ListAssemblyTypesUseCase>();
            warm = await MeasureMs("WARM (2nd call)", () => uc.ExecuteAsync(_dllPath, null));
        }

        _out.WriteLine($"  => Speedup: {cold / warm:F1}x");
        Assert.True(warm < cold, "Cached call should be faster than cold call");
    }

    [Fact]
    public async Task Benchmark_DecompileType_ColdVsWarm()
    {
        _out.WriteLine($"\n=== DecompileType: System.Text.Json.JsonSerializer ===");

        double cold, warm;
        using (var scope = _sp.CreateScope())
        {
            var uc = scope.ServiceProvider.GetRequiredService<DecompileTypeUseCase>();
            cold = await MeasureMs("COLD (1st call)", () => uc.ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer", null));
        }
        using (var scope = _sp.CreateScope())
        {
            var uc = scope.ServiceProvider.GetRequiredService<DecompileTypeUseCase>();
            warm = await MeasureMs("WARM (2nd call)", () => uc.ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer", null));
        }

        _out.WriteLine($"  => Speedup: {cold / warm:F1}x");
        Assert.True(warm < cold, "Cached decompile should be faster than cold decompile");
    }

    [Fact]
    public async Task Benchmark_DecompileType_ConcurrentX10()
    {
        _out.WriteLine($"\n=== Concurrent DecompileType x10 ===");

        // Warm up cache first
        using (var scope = _sp.CreateScope())
        {
            var uc = scope.ServiceProvider.GetRequiredService<DecompileTypeUseCase>();
            await uc.ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer", null);
        }

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            using var scope = _sp.CreateScope();
            var uc = scope.ServiceProvider.GetRequiredService<DecompileTypeUseCase>();
            return await uc.ExecuteAsync(_dllPath, "System.Text.Json.JsonSerializer", null);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        _out.WriteLine($"  Total: {sw.Elapsed.TotalMilliseconds:F1} ms, avg: {sw.Elapsed.TotalMilliseconds / 10:F1} ms/req");
        Assert.All(results, r => Assert.NotEmpty(r));
    }

    [Fact]
    public async Task Benchmark_SearchMembers_ColdVsWarm()
    {
        _out.WriteLine($"\n=== SearchMembers: 'Serialize' ===");

        double cold, warm;
        using (var scope = _sp.CreateScope())
        {
            var uc = scope.ServiceProvider.GetRequiredService<SearchMembersByNameUseCase>();
            cold = await MeasureMs("COLD (1st call)", () => uc.ExecuteAsync(_dllPath, "Serialize", null));
        }
        using (var scope = _sp.CreateScope())
        {
            var uc = scope.ServiceProvider.GetRequiredService<SearchMembersByNameUseCase>();
            warm = await MeasureMs("WARM (2nd call)", () => uc.ExecuteAsync(_dllPath, "Serialize", null));
        }

        _out.WriteLine($"  => Speedup: {cold / warm:F1}x");
        Assert.True(warm < cold, "Cached search should be faster than cold search");
    }
}
