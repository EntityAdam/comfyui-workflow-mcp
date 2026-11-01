# Architecture

ComfyUI Workflow MCP keeps a small public tool surface and delegates execution
state to ComfyUI.

## Public MCP tools

The workflow-facing tools are:

- `setup()` (read-only status and guidance)
- `workflow_configure()`
- `workflow_parameterize()`
- `workflow_unparameterize()`
- `workflow_set_default()`
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

`workflow_list()` returns only workflow IDs, descriptions, and media types.
`workflow_get_options()` returns detailed configuration and parameters on demand.
Workflow mutations return compact change receipts; empty change lists are omitted.

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

The focused workflow tools edit copied JSON in place, add typed placeholders,
merge YAML settings, restore defaults as literals, and persist default workflows
for image, audio, and video. `setup()` only inspects state and returns guidance.

## Input media

`comfy_media_upload()` sends image, audio, and video references through
ComfyUI's upload endpoint. Assets live in the configured root's `assets/`
directory. Agents use `asset_list()` and filenames for existing files.
`asset_stage()` accepts bytes supplied directly by a client; agents should
never generate or reproduce base64 media.

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
