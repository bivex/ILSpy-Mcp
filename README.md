# ILSpy MCP Server

A Model Context Protocol (MCP) server built on .NET 9 that provides in-memory .NET assembly decompilation and structural analysis capabilities for AI assistants using [ILSpy (`ICSharpCode.Decompiler`)](https://github.com/icsharpcode/ILSpy).

---

## 💡 How It Works

ILSpy MCP Server acts as a bridge between AI assistants (like Claude Code, Cursor, Antigravity) and compiled .NET binaries (`.dll`, `.exe`). It enables LLMs to inspect, decompile, and analyze compiled .NET code directly via natural language.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                             AI Client / Host                                │
│                     (Claude Code, Cursor, Antigravity)                      │
└───────────────────────────────────┬─────────────────────────────────────────┘
                                    │ stdio (JSON-RPC 2.0)
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                             ILSpy MCP Server                                │
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────────┐  │
│  │ Transport Layer (MCP Tools)                                           │  │
│  │ Mappings: Tool Request ➔ Parameters Validation ➔ Response Formatting  │  │
│  └──────────────────────────────────┬────────────────────────────────────┘  │
│                                     │                                       │
│  ┌──────────────────────────────────▼────────────────────────────────────┐  │
│  │ Application Use Cases                                                 │  │
│  │ Single-responsibility orchestrators (Timeouts, Cancellation, Limits)   │  │
│  └──────────────────────────────────┬────────────────────────────────────┘  │
│                                     │                                       │
│  ┌──────────────────────────────────▼────────────────────────────────────┐  │
│  │ Infrastructure (ILSpy Engine Adapter)                                 │  │
│  │ Wraps ICSharpCode.Decompiler CSharpDecompiler & PEFile Metadata       │  │
│  └──────────────────────────────────┬────────────────────────────────────┘  │
└─────────────────────────────────────┼───────────────────────────────────────┘
                                      │ In-Memory Inspection
                                      ▼
                      ┌───────────────────────────────┐
                      │  Target .NET Assemblies       │
                      │   (.dll / .exe binaries)      │
                      └───────────────────────────────┘
```

### Request Lifecycle & Processing Pipeline

1. **JSON-RPC Communication**: The MCP server runs as a background process communicating over standard input/output (`stdio`) using JSON-RPC 2.0. Logging is redirected to `stderr` to preserve standard output for protocol payloads.
2. **Tool Selection & Parameter Mapping**: When an AI client invokes a tool (e.g., `decompile_type` or `analyze_assembly`), the **Transport Layer** receives the call, validates inputs using strongly-typed domain models (`AssemblyPath`, `TypeName`), and prevents path traversal or missing file errors early.
3. **Use Case Execution**: The **Application Layer** invokes a dedicated, single-responsibility use case. It sets up `CancellationToken` linked to configurable operation timeouts and size safety limits.
4. **ILSpy Engine Processing**: The **Infrastructure Layer** passes the file path to `ICSharpCode.Decompiler.CSharp.CSharpDecompiler` and `PEFile`:
   - Parses the PE metadata tables and TypeDefs/MethodDefs.
   - Reconstructs C# Abstract Syntax Trees (AST).
   - Generates formatted, human-readable C# source code or structural metadata in-memory.
5. **Response Formatting & Truncation**: Output size is checked against `MaxDecompilationSize` to protect the LLM context window. The formatted output is sent back to the LLM.

---

## 🎯 Recommended LLM Workflow

For best results when analyzing unfamiliar .NET libraries or assemblies, AI assistants follow a 3-step workflow:

```
  Step 1: DISCOVERY                Step 2: STRUCTURAL INSPECTION         Step 3: DECOMPILATION
┌─────────────────────────┐      ┌───────────────────────────────┐     ┌───────────────────────┐
│ `analyze_assembly`      │ ───► │ `get_type_members`            │ ──► │ `decompile_type`      │
│ `list_assembly_types`   │      │ `find_type_hierarchy`         │     │ `decompile_method`    │
└─────────────────────────┘      │ `search_members_by_name`      │     └───────────────────────┘
                                 │ `find_extension_methods`      │
                                 └───────────────────────────────┘
```

1. **Discovery**: Understand overall structure and namespaces using `analyze_assembly` or filter types using `list_assembly_types`.
2. **Structural Inspection**: Understand type signatures, public members, and inheritance graphs using `get_type_members`, `find_type_hierarchy`, or `find_extension_methods` without loading full implementation bodies.
3. **Targeted Decompilation**: Decompile exact class logic or specific method algorithms using `decompile_type` or `decompile_method`.

---

## 🛠️ Available MCP Tools

| Tool | Purpose | Description | Key Parameters |
|------|---------|-------------|----------------|
| **`analyze_assembly`** | High-level Architecture | High-level summary of namespaces, public/internal type counts, and key entry points. | `assemblyPath`, `query` |
| **`list_assembly_types`** | Type Discovery | Lists all types grouped by namespace. Supports namespace filtering. | `assemblyPath`, `namespaceFilter` |
| **`get_type_members`** | API Surface Inspection | Lists methods, properties, fields, and events for a type without decompiling code bodies. | `assemblyPath`, `typeName` |
| **`find_type_hierarchy`** | Inheritance Mapping | Displays base classes, implemented interfaces, and derived types. | `assemblyPath`, `typeName` |
| **`decompile_type`** | Full Type Decompilation | Decompiles complete C# source code of a specified class, interface, struct, or enum. | `assemblyPath`, `typeName` |
| **`decompile_method`** | Method Decompilation | Decompiles only the body and signature of a specific method. | `assemblyPath`, `typeName`, `methodName` |
| **`search_members_by_name`** | Member Search | Searches for members matching a substring or pattern across the assembly. | `assemblyPath`, `searchTerm` |
| **`find_extension_methods`** | Extension Discovery | Finds all C# extension methods defined in the assembly that target a specific type. | `assemblyPath`, `targetTypeName` |

---

## 🚀 Quick Start

### Prerequisites

- **.NET 9.0 SDK** or higher
- An MCP-compatible client (Claude Code, Cursor, Antigravity, Claude Desktop, VS Code, etc.)

### Installation

Install `ILSpyMcp.Server` globally via NuGet:

```bash
dotnet tool install -g ILSpyMcp.Server
```

To update to the latest release:

```bash
dotnet tool update -g ILSpyMcp.Server
```

---

## ⚙️ MCP Client Configuration

### Claude Code
Register directly with the CLI:

```bash
claude mcp add ilspy-mcp --command "ilspy-mcp" --scope user
```

Or add to `.mcp.json` in your workspace:

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

### Cursor & Antigravity
Add to your MCP server configuration:

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

---

## 💬 Natural Language Prompt Examples

- **Analyze Assembly Structure**:
  > *"Analyze the architecture of `/path/to/MyService.dll` and list key entry points."*
- **Explore Types**:
  > *"List all public types in the namespace `MyService.Core` inside `/path/to/MyService.dll`."*
- **Inspect API Surface**:
  > *"Show me all public methods and properties of `UserManager` in `/path/to/MyService.dll`."*
- **Decompile Class Implementation**:
  > *"Decompile the `OrderProcessor` class in `/path/to/ECommerce.dll` and explain how payments are processed."*
- **Decompile Specific Method**:
  > *"Decompile the `ValidateToken` method of `JwtHandler` from `/path/to/Auth.dll`."*

---

## 🔧 Environment Configuration

Customize execution behavior via environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `ILSpy__MaxDecompilationSize` | Maximum allowed output size in bytes (protects context windows) | `1048576` (1 MB) |
| `ILSpy__DefaultTimeoutSeconds` | Operation timeout limit in seconds | `30` |
| `ILSpy__MaxConcurrentOperations` | Maximum concurrent decompilation threads | `10` |

Example setting in `.mcp.json`:

```json
{
  "mcpServers": {
    "ilspy-mcp": {
      "command": "ilspy-mcp",
      "env": {
        "ILSpy__MaxDecompilationSize": "2097152",
        "ILSpy__DefaultTimeoutSeconds": "60"
      }
    }
  }
}
```

---

## 🏗️ Architecture & Security

- **Clean Hexagonal Architecture**: Strictly decoupled into `Domain`, `Application`, `Infrastructure`, and `Transport`.
- **Read-Only Operation**: The server performs pure read-only disassembly and reflection; it never writes or modifies files on disk.
- **Path Validation**: Input assembly paths are checked against illegal characters, path traversal attempts, and file existence.
- **Context Protection**: Decompilation output is limited by configurable byte size boundaries to avoid blowing LLM context budgets.
- **Stderr Logging**: All diagnostic logging goes to `stderr`, leaving `stdout` dedicated strictly for MCP protocol communication.

---

## 📜 License

Distributed under the [MIT License](LICENSE).

