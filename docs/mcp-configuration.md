# MCP client configuration

## Minimal configuration

This configuration runs the pinned NuGet package over standard input and
output. It connects to ComfyUI at `http://localhost:8188`.

```json
{
  "mcpServers": {
    "comfyui": {
      "command": "dnx",
      "args": [
        "ComfyUi.WorkflowMcp@0.0.1-preview5",
        "--yes",
        "--",
        "--stdio"
      ]
    }
  }
}
```

Some clients require `"type": "stdio"` alongside `command`, while others use
`servers` instead of `mcpServers` as the top-level property. Restart or refresh
the MCP client after changing its configuration.

## Run from source without DNX

Use `dotnet run` to test the source tree without publishing a package:

```json
{
  "mcpServers": {
    "comfyui": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/comfyui-workflow-mcp/src/ComfyUi.WorkflowMcp/ComfyUi.WorkflowMcp.csproj",
        "--",
        "--stdio"
      ],
      "env": {
        "ComfyWorkflowMcp__BaseUrl": "http://localhost:8188"
      }
    }
  }
}
```

## Full configuration

Environment variables in an MCP server's `env` object override the defaults.
Replace the example paths with absolute paths valid on the machine running the
MCP server.

```json
{
  "mcpServers": {
    "comfyui": {
      "type": "stdio",
      "command": "dnx",
      "args": [
        "ComfyUi.WorkflowMcp@0.0.1-preview5",
        "--yes",
        "--",
        "--stdio"
      ],
      "env": {
        "ComfyWorkflowMcp__BaseUrl": "http://192.168.1.50:8188",
        "ComfyWorkflowMcp__ServerUrl": "http://localhost:9001",
        "ComfyWorkflowMcp__Directory": "/absolute/path/to/comfyui-workflow-mcp"
      }
    }
  }
}
```

Only include settings that differ from their defaults. The package does not
include workflow JSON, YAML configuration, or input media. Put workflow JSON
under the root's `workflows` subdirectory and run `setup()` to configure it.

## ComfyUI at another URL

Set `ComfyWorkflowMcp__BaseUrl` when ComfyUI listens on another port, hostname,
or trusted machine. The URL must be reachable from the machine that runs the
MCP server. Do not append an API path.

## Local input media

`<ComfyWorkflowMcp__Directory>/assets` is the filesystem boundary. Use
`asset_stage()` to save client-supplied media there; call
`comfy_media_upload()` with only its returned filename.

On Windows, JSON paths must use escaped backslashes such as
`"C:\\ComfyUI\\input"`, or forward slashes such as `"C:/ComfyUI/input"`.

## Network trust

The server is intended for the local machine or a trusted local network. The
HTTP transport does not provide authentication, so do not expose it to the
public internet or an untrusted network.
