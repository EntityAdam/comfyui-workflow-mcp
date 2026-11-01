# Architecture

ComfyUI Workflow MCP keeps a small public tool surface and delegates execution
state to ComfyUI.

## Public MCP tools

The workflow-facing tools are:

- `setup()`
- `workflow_list()`
- `workflow_get()`
- `workflow_get_options()`
- `generate_image()`
- `generate_audio()`
- `generate_video()`

Direct ComfyUI interaction is limited to:

- `comfy_media_upload()`
- `asset_stage()`
- `asset_list()`
- `asset_exists()`
- `asset_view()`
- `comfy_job_cancel()`
- `comfy_queue_list()`
- `comfy_queue_clear()`

Workflow execution helpers are internal APIs, not additional MCP tools.

## Completion modes

The default backing path submits a workflow and waits for completion using
ComfyUI's WebSocket and history APIs. The completed media is returned to the
MCP client for display.

When `wait_for_completion` is `false`, the generation tool submits through
ComfyUI's HTTP API and returns without waiting. The job remains visible in
ComfyUI's queue and may be managed with the queue tools.

ComfyUI reference implementations:

- [WebSocket and history completion](https://docs.comfy.org/development/comfyui-server/api-examples#method-2-websocket-+-history-monitor-completion)
- [Submit and forget over HTTP](https://docs.comfy.org/development/comfyui-server/api-examples#method-1-submit-and-forget-http-only)

## Result handling

Each generation tool requests one media type. When ComfyUI returns multiple
items, the server returns only the first output of the requested type. Outputs
of other types are ignored for that call.

There is no MCP-side asset registry, publication pipeline, or TTL. Waiting calls
return their result directly; fire-and-forget calls leave output management to
ComfyUI.

## Workflow and YAML storage

API-format workflow JSON and YAML configuration live in user-selected external
directories. Neither directory is bundled with the package.

Workflow JSON is the executable graph. YAML records defaults and parameter
constraints learned from ComfyUI validation failures, allowing future calls to
reject known-invalid values before submission.

Setup may edit copied workflow JSON in place, add typed placeholders, write
YAML, change workflow discovery paths, and persist default workflows for image,
audio, and video generation.

## Input media

`comfy_media_upload()` sends image, audio, and video references through
ComfyUI's upload endpoint. Local paths must be in `assets`, created by
`asset_stage`, beside the executable.

`asset_view(filename)` reads an image from the configured root's `assets`
directory and returns a standard MCP `ImageContentBlock` with base64 bytes
and a MIME type, plus filename and byte-count metadata. It does not call
ComfyUI or an LLM endpoint. Model selection, vision support, and forwarding
the image into the conversation's model context belong to the MCP host;
standard MCP client capabilities do not identify the active model's vision
support.

## Trust model

The MCP server and ComfyUI are assumed to run on the same machine or a trusted
local network. The project does not add a remote asset host, publication service,
or authentication layer.
