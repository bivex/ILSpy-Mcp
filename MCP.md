# ILSpy MCP Server - Connection Guide

This guide explains how to connect the ILSpy MCP server to various MCP clients.

## Prerequisites

- .NET 10.0 SDK installed
- An MCP-compatible client (Claude Code, Cursor, Claude Desktop)

## Installation

Install as a global dotnet tool:

```bash
dotnet tool install -g ILSpyMcp.Server
```

Verify the installation:

```bash
ilspy-mcp --help
```

## Client Configuration

### Claude Code

Register the server globally:

```bash
claude mcp add ilspy-mcp --command "ilspy-mcp" --scope user
```

Or per-project — create/update `.mcp.json` in the repo root:

```json
{
  "mcpServers": {
    "ilspy-mcp": {
      "type": "stdio",
      "command": "ilspy-mcp",
      "args": []
    }
  }
}
```

### Cursor

Add to your Cursor MCP configuration (`~/.cursor/mcp.json`):

```json
{
  "mcpServers": {
    "ilspy-mcp": {
      "command": "ilspy-mcp",
      "args": [],
      "env": {
        "ILSpy__MaxDecompilationSize": "1048576",
        "ILSpy__DefaultTimeoutSeconds": "30",
        "ILSpy__MaxConcurrentOperations": "10"
      },
      "disabled": false,
      "autoApprove": [
        "decompile_type",
        "decompile_method",
        "list_assembly_types",
        "analyze_assembly",
        "get_type_members",
        "find_type_hierarchy",
        "search_members_by_name",
        "find_extension_methods"
      ]
    }
  }
}
```

### Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "ilspy-mcp": {
      "command": "ilspy-mcp",
      "args": []
    }
  }
}
```

## Configuration Options

The server can be configured via environment variables:

- `ILSpy__MaxDecompilationSize`: Maximum size of decompiled code in bytes (default: 1048576 = 1 MB)
- `ILSpy__DefaultTimeoutSeconds`: Default timeout for operations in seconds (default: 30)
- `ILSpy__MaxConcurrentOperations`: Maximum number of concurrent operations (default: 10)

## Available Tools

Once connected, the following tools will be available:

- **decompile_type**: Decompile and analyze a .NET type
- **decompile_method**: Decompile and analyze a specific method
- **list_assembly_types**: List all types in an assembly
- **analyze_assembly**: Get architectural overview of an assembly
- **get_type_members**: Get complete API surface of a type
- **find_type_hierarchy**: Find inheritance relationships
- **search_members_by_name**: Search for members by name
- **find_extension_methods**: Find extension methods for a type

## Usage Example

After configuring, the server is available immediately. You can use tools like:

```
decompile_type(
  assemblyPath: "/path/to/assembly.dll",
  typeName: "System.String",
  query: "What methods are available?"
)
```

## Troubleshooting

### Server Not Starting

- Verify .NET 10.0 SDK is installed: `dotnet --version`
- Verify the tool is installed: `dotnet tool list -g | grep ILSpyMcp`
- Try reinstalling: `dotnet tool uninstall -g ILSpyMcp.Server && dotnet tool install -g ILSpyMcp.Server`

### Tools Not Available

- Check that the MCP server is registered: run `/mcp` in Claude Code
- Verify the server started successfully in your client's MCP panel

### Timeout Errors

- Increase `ILSpy__DefaultTimeoutSeconds` for large assemblies
- Consider increasing `ILSpy__MaxDecompilationSize` if decompilation fails

## Security Notes

- All operations are **read-only** (no file modifications)
- Assembly paths are validated before processing
- Operations have timeout protection to prevent resource exhaustion

## Updating

```bash
dotnet tool update -g ILSpyMcp.Server
```

## Logging

Logs are written to stderr. To adjust log levels, set environment variables:

```bash
Logging__LogLevel__Default=Information
Logging__LogLevel__ILSpy.Mcp=Debug
```
