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

`setup()` shows paths and a guide. The agent then uses focused workflow tools
to configure API-format workflow exports and select defaults for image, audio,
or video generation. ComfyUI may be on the same machine or a
trusted local network.

## Setup

The package does not include workflow or YAML files. During setup, the agent can:

1. Inspect paths with `setup()` and discover workflows with `workflow_list()`.
2. Configure YAML with `workflow_configure()`.
3. Replace configurable node inputs with typed parameters using
   `workflow_parameterize()`.
4. Add YAML defaults, allowed values, and constraints for parameters that
   ComfyUI validates only when a workflow runs.
5. Select a default for image, audio, or video with `workflow_set_default()`.

Source workflow files may be edited directly. They are expected to be copies
exported from ComfyUI and do not require version-preserving transformations.

Workflow configuration updates are additive: send only new or changed parameter bindings and
YAML configuration entries. Existing defaults and validation rules are
preserved unless explicitly updated or removed with `null`.
`workflow_unparameterize(workflow_id: "portrait", parameter_names: ["cfg"])`
restores the selected parameter's current YAML default as a fixed workflow input
and removes its configuration. See [configuration](docs/configuration.md#incremental-yaml-configuration)
for update and removal examples. Workflow edits return compact change receipts;
`setup()` is read-only and takes no arguments.

## Tools

### Generation

- `generate_image()` runs an image workflow.
- `generate_audio()` runs an audio workflow.
- `generate_video()` runs a video workflow.
- `workflow_list()` lists discovered workflow IDs, descriptions from workflow
  YAML when configured, and media types. Parameters and validation are returned
  only by `workflow_get_options()`.
- `workflow_get(workflow_id)` returns one API-format workflow JSON for node and
  input inspection; `workflow_id` is mandatory.
- `workflow_get_options(workflow_id)` returns YAML-backed defaults, allowed
  values, constraints, and discovered parameters for one workflow.

Each generation call requires `workflow_id` unless `workflow_set_default()`
has selected a default for that media type.

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
- `asset_stage()` saves media bytes supplied directly by the client in `assets/`.
  Agents should use `asset_list()` and filenames for existing media and never
  generate or reproduce base64.
- `asset_list()` lists recognized image, video, and audio files in `assets/`
  by filename extension, omitting other files. Supported extensions are PNG,
  JPG/JPEG, WebP, GIF, MP4, AVI, WebM, MOV, MP3, WAV, FLAC, and OGG.
- `asset_exists()` validates a filename in `assets/`.
- `asset_view(filename)` returns an image from `assets/` as MCP image content
  for clients that forward tool images to a vision-capable model.
- `comfy_diffusion_model_list()` lists installed diffusion models.
- `comfy_job_cancel()` attempts to cancel a running job.
- `comfy_queue_list()` reports running and pending jobs.
- `comfy_queue_clear()` removes pending jobs from the queue.

There is no MCP-managed asset registry, publishing layer, or output TTL. A
completed result is returned directly to the client; a fire-and-forget result
remains a ComfyUI job.

## Input media

Local uploads are limited to the `assets` subdirectory of
`ComfyWorkflowMcp__Directory`. Use `asset_list()` to find existing media and
pass its filename to `comfy_media_upload()`. Clients can supply attachment bytes
directly through `asset_stage()`; agents should not transcribe base64. When a
client cannot supply bytes directly, place the file in `assets/`.

Set `ComfyWorkflowMcp__Directory` to the root that should contain
`settings.yaml`, `workflows`, `assets`, and `output`. See
[Configuration](docs/configuration.md) for details.

`asset_view()` accepts PNG, JPEG, WebP, and GIF filenames, including files
placed directly in `assets/`. It rejects directory paths, symbolic links,
empty files, and files larger than 100 MB. MIME types are inferred from the
filename; the server does not decode or convert images.

The MCP server cannot detect the client's active model or its vision support.
The client controls whether returned images enter the model's context. See
[LM Studio compatibility](docs/mcp-configuration.md#lm-studio-image-viewing)
before relying on visual inspection in LM Studio.

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
