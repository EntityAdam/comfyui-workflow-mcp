# ComfyUI Workflow MCP Server for .NET

[![Release](https://github.com/EntityAdam/comfyui-workflow-mcp/actions/workflows/release.yml/badge.svg)](https://github.com/EntityAdam/comfyui-workflow-mcp/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/vpre/ComfyUi.WorkflowMcp.svg)](https://www.nuget.org/packages/ComfyUi.WorkflowMcp)
[![License: MIT](https://img.shields.io/github/license/EntityAdam/comfyui-workflow-mcp.svg)](LICENSE)

<!-- mcp-name: io.github.EntityAdam/comfyui-workflow-mcp -->

Run parameterized ComfyUI workflows for image, audio, and video generation
through the Model Context Protocol.

## Quickstart

### 1. Install .NET 10

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
The SDK includes `dnx`, which downloads and runs the pinned NuGet package
without a permanent installation.

Start ComfyUI normally and leave it available at `http://localhost:8188`.

### 2. Paste the server configuration

Add this server to your MCP configuration:

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

See [MCP client configuration](docs/mcp-configuration.md) for a complete
configuration and copy-paste alternatives, including connecting to ComfyUI at
another URL.

Some clients call the top-level property `servers` instead of `mcpServers`.
Restart or refresh the MCP client after saving the configuration.

### 3. Let your agent finish setup

Ask:

> Run `setup()` and help me configure ComfyUI Workflow MCP.

Setup can inspect the configured root, parameterize ComfyUI API-format workflow
exports, and save a default workflow
for image, audio, or video generation. ComfyUI may be on the same machine or a
trusted local network.

## Setup

The package does not include workflow or YAML files. During setup, the agent can:

1. Inspect the configured root and discovered workflows.
2. Configure YAML stored beside its workflow JSON.
3. Edit a copied ComfyUI API-format JSON workflow and replace configurable node
   inputs with typed parameters.
4. Add YAML defaults, allowed values, and constraints for parameters that
   ComfyUI validates only when a workflow runs.
5. Save a default workflow for image, audio, or video generation.

Source workflow files may be edited directly. They are expected to be copies
exported from ComfyUI and do not require version-preserving transformations.

## Tools

### Generation

- `generate_image()` runs an image workflow.
- `generate_audio()` runs an audio workflow.
- `generate_video()` runs a video workflow.
- `workflow_list()` lists discovered workflow IDs and their parameters.
- `workflow_get(workflow_id)` returns one API-format workflow JSON for node and
  input inspection; `workflow_id` is mandatory.
- `workflow_get_options(workflow_id)` returns YAML-backed defaults, allowed
  values, constraints, and discovered parameters for one workflow.

Each generation call requires `workflow_id` unless setup has saved a default
for that media type. If neither is available, the tool recommends running
`setup()`.

Generation uses the workflow YAML's `wait_for_completion` value, defaulting to
`true`, and returns media for the MCP client to display when waiting. Set the
generation argument explicitly to override the workflow default;
the tool queues the workflow on ComfyUI and returns its prompt ID without
waiting for media. The wait and submit paths are internal APIs, not separate
MCP tools.

Completed generation calls save a uniquely named copy under the configured
root's `output` subdirectory. The result includes `saved_path` and
`output_filename`. Submit-only calls do not save a local copy.

If a workflow produces more than one output, the server returns only the first
output matching the requested media type.

### ComfyUI controls

- `comfy_media_upload()` uploads an image, audio file, or video through
  ComfyUI's upload endpoint.
- `asset_stage()` saves base64 client media in `assets/` for upload.
- `asset_list()` lists media staged in `assets/`.
- `asset_exists()` validates a filename in `assets/`.
- `comfy_diffusion_model_list()` lists installed diffusion models.
- `comfy_job_cancel()` attempts to cancel a running job.
- `comfy_queue_list()` reports running and pending jobs.
- `comfy_queue_clear()` removes pending jobs from the queue.

There is no MCP-managed asset registry, publishing layer, or output TTL. A
completed result is returned directly to the client; a fire-and-forget result
remains a ComfyUI job.

## Input media

Local uploads are limited to the `assets` subdirectory of
`ComfyWorkflowMcp__Directory`. Use `asset_stage()` to write client-provided
base64 media there; the server reads only the exact path supplied to
`comfy_media_upload()`.

Set `ComfyWorkflowMcp__Directory` to the root that should contain
`settings.yaml`, `workflows`, `assets`, and `output`. See
[Configuration](docs/configuration.md) for details.

## How-to guides

- [Generate audio from text](docs/how-to/text_to_audio.md)
- [Generate an image from text](docs/how-to/text_to_image.md)
- [Edit an image](docs/how-to/image_edit.md)
- [Upscale an image](docs/how-to/image_upscale.md)
- [Generate video from text](docs/how-to/text_to_video.md)
- [Interpolate video frames](docs/how-to/video_frame_interpolation.md)
- [Stop or remove a DNX-launched server](docs/how-to/stop_and_remove_dnx.md)

## More documentation

- [Architecture](docs/architecture.md)
- [MCP client configuration](docs/mcp-configuration.md)
- [Configuration](docs/configuration.md)
- [Run from source](docs/run_from_source.md)
- [Build and release](docs/build_and_release.md)

Self-contained Windows, Linux, and macOS archives are attached to each
[GitHub release](https://github.com/EntityAdam/comfyui-workflow-mcp/releases).

## Acknowledgements

Ported technical requirements from
[artokun/comfyui-mcp](https://github.com/artokun/comfyui-mcp). Thank you!
