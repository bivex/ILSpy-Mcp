using Xunit;
using FluentAssertions;
using ILSpy.Mcp.Application.Configuration;
using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Application.UseCases;
using ILSpy.Mcp.Domain.Errors;
using ILSpy.Mcp.Domain.Models;
using ILSpy.Mcp.Domain.Services;
using ILSpy.Mcp.Infrastructure.Decompiler;
using ILSpy.Mcp.Transport.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

namespace ILSpy.Mcp.Tests;

/// <summary>
/// Comprehensive unit and integration tests designed to catch bugs, edge cases,
/// validation errors, concurrency issues, and exception handling corner cases.
/// </summary>
public class BugFindingTests : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly string _validAssemblyPath;

    public BugFindingTests()
    {
        var runtimePath = RuntimeEnvironment.GetRuntimeDirectory();
        var possibleAssemblies = new[]
        {
            Path.Combine(runtimePath, "System.Private.CoreLib.dll"),
            Path.Combine(runtimePath, "System.Runtime.dll"),
            Path.Combine(runtimePath, "System.Collections.dll")
        };

        _validAssemblyPath = possibleAssemblies.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException($"No suitable test assembly found in: {runtimePath}");

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        services.Configure<ILSpyOptions>(options =>
        {
            options.DefaultTimeoutSeconds = 30;
            options.MaxDecompilationSize = 1000; // Small size limit to test truncation boundary
            options.MaxConcurrentOperations = 10;
        });

        services.AddSingleton<ITimeoutService, TimeoutService>();
        services.AddScoped<IDecompilerService, ILSpyDecompilerService>();

        services.AddScoped<DecompileTypeUseCase>();
        services.AddScoped<DecompileMethodUseCase>();
        services.AddScoped<ListAssemblyTypesUseCase>();
        services.AddScoped<AnalyzeAssemblyUseCase>();
        services.AddScoped<GetTypeMembersUseCase>();
        services.AddScoped<FindTypeHierarchyUseCase>();
        services.AddScoped<SearchMembersByNameUseCase>();
        services.AddScoped<FindExtensionMethodsUseCase>();

        services.AddScoped<DecompileTypeTool>();
        services.AddScoped<DecompileMethodTool>();

        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }

    #region Domain Model Edge Cases

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AssemblyPath_Create_NullOrWhitespace_ThrowsArgumentException(string? invalidPath)
    {
        Action act = () => AssemblyPath.Create(invalidPath!);
        act.Should().Throw<ArgumentException>()
           .WithMessage("*null or empty*");
    }

    [Fact]
    public void AssemblyPath_Create_NonExistentFile_ThrowsFileNotFoundException()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "NonExistentAssembly_12345.dll");
        Action act = () => AssemblyPath.Create(nonExistentPath);
        act.Should().Throw<FileNotFoundException>();
    }

    [Theory]
    [InlineData("test.txt")]
    [InlineData("test.json")]
    [InlineData("test.so")]
    [InlineData("test.dylib")]
    [InlineData("test.pdb")]
    public void AssemblyPath_Create_UnsupportedExtension_ThrowsArgumentException(string fileName)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(tempFile, "dummy content");
        try
        {
            Action act = () => AssemblyPath.Create(tempFile);
            act.Should().Throw<ArgumentException>()
               .WithMessage("*Invalid assembly file extension*");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TypeName_Create_NullOrWhitespace_ThrowsArgumentException(string? invalidName)
    {
        Action act = () => TypeName.Create(invalidName!);
        act.Should().Throw<ArgumentException>()
           .WithMessage("*Type name cannot be null or empty*");
    }

    [Fact]
    public void TypeName_Create_ValidQualifiedName_ParsesNamespaceAndShortNameCorrectly()
    {
        var typeName = TypeName.Create("System.Collections.Generic.List`1");
        typeName.FullName.Should().Be("System.Collections.Generic.List`1");
        typeName.Namespace.Should().Be("System.Collections.Generic");
        typeName.ShortName.Should().Be("List`1");
    }

    [Fact]
    public void TypeName_Create_GlobalTypeWithoutNamespace_ReturnsNullNamespace()
    {
        var typeName = TypeName.Create("GlobalType");
        typeName.FullName.Should().Be("GlobalType");
        typeName.Namespace.Should().BeNull();
        typeName.ShortName.Should().Be("GlobalType");
    }

    #endregion

    #region Infrastructure & Decompiler Edge Cases

    [Fact]
    public async Task DecompileTypeAsync_NonExistentType_ThrowsTypeNotFoundException()
    {
        using var scope = _serviceProvider.CreateScope();
        var decompiler = scope.ServiceProvider.GetRequiredService<IDecompilerService>();
        var assemblyPath = AssemblyPath.Create(_validAssemblyPath);
        var typeName = TypeName.Create("NonExistentNamespace.FakeClass123");

        Func<Task> act = async () => await decompiler.DecompileTypeAsync(assemblyPath, typeName);

        await act.Should().ThrowAsync<TypeNotFoundException>();
    }

    [Fact]
    public async Task DecompileMethodAsync_NonExistentMethod_ThrowsMethodNotFoundException()
    {
        using var scope = _serviceProvider.CreateScope();
        var decompiler = scope.ServiceProvider.GetRequiredService<IDecompilerService>();
        var assemblyPath = AssemblyPath.Create(_validAssemblyPath);
        var typeName = TypeName.Create("System.Collections.BitArray");

        Func<Task> act = async () => await decompiler.DecompileMethodAsync(assemblyPath, typeName, "NonExistentMethod999");

        await act.Should().ThrowAsync<MethodNotFoundException>();
    }

    [Fact]
    public async Task DecompileTypeAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        using var scope = _serviceProvider.CreateScope();
        var decompiler = scope.ServiceProvider.GetRequiredService<IDecompilerService>();
        var assemblyPath = AssemblyPath.Create(_validAssemblyPath);
        var typeName = TypeName.Create("System.Collections.BitArray");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        Func<Task> act = async () => await decompiler.DecompileTypeAsync(assemblyPath, typeName, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Concurrency & Multithreading Bug Detection

    [Fact]
    public async Task DecompilerService_ConcurrentRequests_ExecutesThreadSafeWithoutExceptions()
    {
        using var scope = _serviceProvider.CreateScope();
        var decompiler = scope.ServiceProvider.GetRequiredService<IDecompilerService>();
        var assemblyPath = AssemblyPath.Create(_validAssemblyPath);
        var typeName = TypeName.Create("System.String");

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Run(async () => await decompiler.DecompileTypeAsync(assemblyPath, typeName))
        ).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(10);
        results.All(r => !string.IsNullOrEmpty(r.SourceCode)).Should().BeTrue();
    }

    #endregion

    #region MCP Tool Exception & Error Mapping Tests

    [Fact]
    public async Task DecompileTypeTool_NonExistentType_ThrowsMcpToolExceptionWithTypeNotFoundCode()
    {
        using var scope = _serviceProvider.CreateScope();
        var tool = scope.ServiceProvider.GetRequiredService<DecompileTypeTool>();

        Func<Task> act = async () => await tool.ExecuteAsync(_validAssemblyPath, "NonExistentNamespace.FakeClass999");

        var ex = await act.Should().ThrowAsync<ILSpy.Mcp.Transport.Mcp.Errors.McpToolException>();
        ex.Which.ErrorCode.Should().Be("TYPE_NOT_FOUND");
    }

    [Fact]
    public async Task DecompileTypeTool_NonExistentAssembly_ThrowsMcpToolExceptionWithAssemblyLoadFailedCode()
    {
        using var scope = _serviceProvider.CreateScope();
        var tool = scope.ServiceProvider.GetRequiredService<DecompileTypeTool>();
        var missingAssemblyPath = Path.Combine(Path.GetTempPath(), "MissingAssembly_99999.dll");

        Func<Task> act = async () => await tool.ExecuteAsync(missingAssemblyPath, "System.String");

        var ex = await act.Should().ThrowAsync<ILSpy.Mcp.Transport.Mcp.Errors.McpToolException>();
        ex.Which.ErrorCode.Should().Be("ASSEMBLY_LOAD_FAILED");
    }

    [Fact]
    public async Task DecompileTypeUseCase_ValidType_ReturnsDecompiledSourceCode()
    {
        using var scope = _serviceProvider.CreateScope();
        var useCase = scope.ServiceProvider.GetRequiredService<DecompileTypeUseCase>();

        var result = await useCase.ExecuteAsync(_validAssemblyPath, "System.String", query: null);

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("class String");
    }

    #endregion
}
