# Run from source

Use this path when developing or modifying ComfyUI MCP. For ordinary use,
prefer the NuGet package described in the main README.

## Streamable HTTP

Start ComfyUI on port 8188, then run:

```bash
dotnet run --project src/ComfyUi.Mcp
```

The MCP endpoint is `http://localhost:9001/mcp` and the health endpoint is
`http://localhost:9001/health`.

The HTTP server binds to `localhost` by default. To make it reachable from
other devices on a trusted local network:

```bash
dotnet run --project src/ComfyUi.Mcp \
  --no-launch-profile \
  --urls "http://0.0.0.0:9001"
```

Connect clients to the server computer's LAN address, such as
`http://192.168.1.25:9001/mcp`. `0.0.0.0` is a listen address, not a client
destination. The server does not currently provide authentication, so do not
expose it to an untrusted network.

## Standard input/output

Point a local MCP client at the project:

```json
{
  "mcpServers": {
    "comfyui": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/absolute/path/to/src/ComfyUi.Mcp",
        "--",
        "--stdio"
      ]
    }
  }
}
```

Some clients use `servers` rather than `mcpServers` as the top-level property.

## Verify a source build

```bash
dotnet restore
dotnet test --configuration Release
bash scripts/smoke-test-publish.sh
```

For configuration overrides, see [Configuration](configuration.md).
