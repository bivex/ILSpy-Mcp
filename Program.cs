using ILSpy.Mcp.Application.Configuration;
using ILSpy.Mcp.Application.Services;
using ILSpy.Mcp.Application.UseCases;
using ILSpy.Mcp.Domain.Services;
using ILSpy.Mcp.Infrastructure.Decompiler;
using ILSpy.Mcp.Transport.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Disable watching the working dir, which is often the project dir
Environment.SetEnvironmentVariable("DOTNET_hostBuilder:reloadConfigOnChange", "false");

var builder = Host.CreateApplicationBuilder(args);

// Configure logging to stderr
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Configure options
builder.Services.Configure<ILSpyOptions>(
    builder.Configuration.GetSection(ILSpyOptions.SectionName));

// Configure MCP server
var mcpBuilder = builder.Services.AddMcpServer();
mcpBuilder.WithStdioServerTransport();
mcpBuilder.WithToolsFromAssembly();

// Register application services
builder.Services.AddSingleton<ITimeoutService, TimeoutService>();

// Register domain services (ports)
builder.Services.AddScoped<IDecompilerService, ILSpyDecompilerService>();

// Register application use cases
builder.Services.AddScoped<DecompileTypeUseCase>();
builder.Services.AddScoped<DecompileMethodUseCase>();
builder.Services.AddScoped<ListAssemblyTypesUseCase>();
builder.Services.AddScoped<AnalyzeAssemblyUseCase>();
builder.Services.AddScoped<GetTypeMembersUseCase>();
builder.Services.AddScoped<FindTypeHierarchyUseCase>();
builder.Services.AddScoped<SearchMembersByNameUseCase>();
builder.Services.AddScoped<FindExtensionMethodsUseCase>();

// Register MCP tool handlers
builder.Services.AddScoped<DecompileTypeTool>();
builder.Services.AddScoped<DecompileMethodTool>();
builder.Services.AddScoped<ListAssemblyTypesTool>();
builder.Services.AddScoped<AnalyzeAssemblyTool>();
builder.Services.AddScoped<GetTypeMembersTool>();
builder.Services.AddScoped<FindTypeHierarchyTool>();
builder.Services.AddScoped<SearchMembersByNameTool>();
builder.Services.AddScoped<FindExtensionMethodsTool>();

await builder.Build().RunAsync();
