# ComfyUI MCP

A small MCP server for running validated ComfyUI workflows on a local machine or local network.

The server deliberately has no asset database, publishing layer, model families, or background lifecycle store. A workflow either waits and returns its first requested media output, or it is submitted to ComfyUI and left there.

## Install

```bash
dotnet tool install --global ComfyUi.Mcp --prerelease
comfyui-mcp
```

ComfyUI is expected at `http://localhost:8188`. Configure another address with `ComfyMcp__BaseUrl`.

## Tools

- `setup()` inspects and edits workflow setup, JSON parameter bindings, YAML validation, and per-media defaults.
- `workflow_list()` lists saved workflows and their validated parameters.
- `workflow_run()` waits for completion and returns the first output matching the workflow's configured media type.
- `workflow_run_on_server()` submits to ComfyUI and immediately returns its `prompt_id`.
- `generate_image()`, `generate_audio()`, and `generate_video()` are friendly wait-and-return wrappers.
- `comfy_media_upload()` uploads reference media through ComfyUI's `/upload/image` endpoint.
- `comfy_job_cancel()` best-effort cancels one pending or active prompt.
- `comfy_queue_list()` lists running and pending work.
- `comfy_queue_clear()` clears pending work without interrupting the active prompt.

## Execution modes

`workflow_run()` opens a ComfyUI WebSocket, submits the workflow, waits for the matching prompt to complete, reads `/history/{prompt_id}`, downloads the first output of the configured type through `/view`, and returns it directly in the MCP response.

`workflow_run_on_server()` only posts the rendered workflow to `/prompt`. Completed output is intentionally retrieved through ComfyUI itself, not this MCP server.

## Workflow configuration

Workflow JSON files use typed placeholders:

```json
{
  "6": { "inputs": { "text": "PARAM_PROMPT" } },
  "3": { "inputs": { "steps": "PARAM_INT_STEPS" } }
}
```

Each workflow may have `configuration/workflows/<workflow-id>.yaml`:

```yaml
media_type: image
defaults:
  steps: 20
  model: model.safetensors
allowed_values:
  sampler: [euler, dpmpp_2m]
constraints:
  steps:
    minimum: 1
    maximum: 100
  prompt:
    pattern: ".+"
```

YAML rejects known-invalid node values before submission. Tool-call values override defaults.

Use `setup(operation: "parameterize_workflow", ...)` to edit copied API-format JSON files in place, and `setup(operation: "configure_workflow", ...)` to write YAML validation.

## Configuration

- `ComfyMcp__BaseUrl`: ComfyUI URL; default `http://localhost:8188`.
- `ComfyMcp__ServerUrl`: MCP HTTP URL; default `http://localhost:9001`.
- `ComfyMcp__WorkflowDirectory`: initial JSON workflow directory.
- `ComfyMcp__ConfigurationDirectory`: YAML configuration directory.
- `ComfyMcp__InputRoot`: local path boundary for `comfy_media_upload()`.

The setup tool can persist a different workflow directory and default image, audio, and video workflows in `configuration/settings.yaml`.

## Development

```bash
dotnet restore ComfyUi.Mcp.slnx
dotnet test ComfyUi.Mcp.slnx
```

See [configuration](docs/configuration.md), [MCP client configuration](docs/mcp-configuration.md), and [running from source](docs/run_from_source.md).
