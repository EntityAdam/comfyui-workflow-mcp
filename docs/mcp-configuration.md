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
under the root's `workflows` subdirectory and call `setup()` for configuration
guidance. Use the focused workflow tools to apply changes.

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

## LM Studio image viewing

For LM Studio's regular chat interface, use the source configuration above
to try the new tool before it is included in a package release. LM Studio
uses the `mcpServers` configuration format; edit `mcp.json` through the
Program sidebar and refresh the server after building changes. See the
[LM Studio MCP documentation](https://lmstudio.ai/docs/app/mcp).

1. Place a PNG, JPEG, WebP, or GIF in
   `<ComfyWorkflowMcp__Directory>/assets`, or use `asset_stage()`.
2. Load a model with vision and tool-use support.
3. Ask the model to call `asset_list()`, then
   `asset_view(filename="your-image.png")`, and describe the image.

The tool returns the actual image bytes using the standard
[MCP image content format](https://modelcontextprotocol.io/specification/2025-11-25/schema#imagecontent).
MCP's standard client capabilities do not expose the selected model's vision
support, so the server always returns image content for a valid image asset;
it cannot gate the response on the active model.

Displaying an image in chat does not prove that the model received it.
LM Studio's issue tracker has open reports for
[0.4.9](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/1746) and
[0.4.6](https://github.com/lmstudio-ai/lmstudio-bug-tracker/issues/1596)
where MCP image results were displayed or saved but not forwarded to vision
models. These reports do not establish behavior in every later build.
Validate your installed version using an image with visual details absent
from its filename and the prompt, then compare with attaching the same image
directly to the chat. Server integration tests verify the MCP response, not
LM Studio's model-context delivery. If your build drops tool images, attach
the image directly in LM Studio; the MCP server cannot force forwarding.

This workflow targets regular LM Studio chat and does not depend on Bionic.

## Network trust

The server is intended for the local machine or a trusted local network. The
HTTP transport does not provide authentication, so do not expose it to the
public internet or an untrusted network.
