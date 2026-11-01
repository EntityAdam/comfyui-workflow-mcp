# Architecture

ComfyUI Workflow MCP holds planned runs locally until explicit batch playback,
then delegates job execution to ComfyUI.

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
- `workflow_preview()`
- `generate_image()`
- `generate_audio()`
- `generate_video()`
- `batch_count()`
- `batch_remove()`
- `batch_clear()`
- `batch_play()`

Direct ComfyUI interaction is limited to:

- `comfy_media_upload()`
- `asset_stage()`
- `asset_list()`
- `asset_exists()`
- `asset_view()`
- `comfy_job_cancel()`
- `comfy_queue_list()`
- `comfy_queue_clear()`
- `comfy_diffusion_model_list()`

`workflow_list()` returns only workflow IDs, descriptions, and media types.
`workflow_get_options()` returns detailed configuration and parameters on demand.
Workflow mutations return compact change receipts; empty change lists are omitted.
Workflow options include each parameter's node/input bindings. Parameterization
uses a typed mapping schema with required fields and a four-value type enum.
Tool failures share a structured code, message, and recovery step, with MCP's
`isError` flag set. Argument-binding errors are normalized at the MCP boundary.

Workflow execution helpers are internal APIs, not additional MCP tools.

Stdio mode uses a generic host with the MCP stdio transport and logs on stderr;
it does not start an HTTP listener or use the configured server port. HTTP mode
uses the web host and exposes `/health` and `/mcp`.

`WorkflowPreparationService` is shared by preview, held batches, and immediate
generation. Capture resolves scalar and transform options and renders a fresh
template without network calls. Playback applies ordered YAML transforms to the
captured graph and checks the result. Named adapters
support negative rgthree seed replacement and MiniMax ordered reference lists.
They inspect required installed node schemas through ComfyUI's `object_info`
endpoint. Prepared graphs are never saved over templates. See
[workflow transforms](workflow-transforms.md) for adapter contracts and receipts.

## Completion modes

Generation holds a run by default. `BatchQueueStore` persists snapshots and local
submission states using atomic file replacement and storage/playback locks.
`BatchQueueService` plays only the selected pending snapshot, records submission
IDs before waiting, stops on errors, and avoids replay after uncertain dispatch.
There is no automatic worker. See [held batches](batch-queue.md) for recovery and
status semantics.

Playback or explicitly immediate generation can wait for completion using
ComfyUI's WebSocket and history APIs and return media to the MCP client.

When the effective `wait_for_completion` is `false`, playback submits through
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
Configuration updates validate the merged defaults, constraints, allowed values,
and enabled transforms against the workflow before writing. Discovery and
resolution use the same configuration validation and caller-requirement rules.

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
