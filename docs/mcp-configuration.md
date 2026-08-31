# MCP client configuration

## Run the installed tool over stdio

```json
{
  "mcpServers": {
    "comfyui": {
      "command": "comfyui-mcp",
      "args": ["--stdio"],
      "env": {
        "ComfyMcp__BaseUrl": "http://localhost:8188",
        "ComfyMcp__InputRoot": "/absolute/path/to/reference-media"
      }
    }
  }
}
```

## Run over HTTP

Start `comfyui-mcp` without `--stdio`, then connect the MCP client to `http://localhost:9001/mcp`.

Set `ComfyMcp__WorkflowDirectory` and `ComfyMcp__ConfigurationDirectory` when using directories outside the package. The `setup()` tool can subsequently persist another workflow directory and media defaults.
