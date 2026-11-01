# Configuration

## Environment variables

Configuration is bound from the `ComfyWorkflowMcp` section. ASP.NET environment
variables replace `:` with `__`:

- `ComfyWorkflowMcp__BaseUrl` — ComfyUI URL; default `http://localhost:8188`
- `ComfyWorkflowMcp__ServerUrl` — public MCP server URL; default
  `http://localhost:9001`
- `ComfyWorkflowMcp__Directory` — root for `settings.yaml`, workflows, workflow
YAML, the held batch queue, staged assets, and generated output; default is the directory containing the executable

These directories are not bundled. The server can start and hold generation
runs while ComfyUI is offline. Uploads, playback, immediate generation, and
ComfyUI queue operations require ComfyUI. See [held batches](batch-queue.md).

## Media assets

`comfy_media_upload` accepts an image, audio file, or video and sends it through
ComfyUI's upload endpoint. A local file must be inside
`<ComfyWorkflowMcp__Directory>/assets`:

```json
{
  "filename": "source.mp4"
}
```

For existing media, call `asset_list()` and use its filenames with
`comfy_media_upload()` or `asset_view()`. `asset_exists()` checks a filename.
`asset_stage()` is for base64/data-URL bytes supplied directly by the client.
Agents should never generate, transcribe, or reproduce base64 media. If the
client cannot supply the bytes directly, place the file in `assets/` and use
its filename.

The returned `workflow_value` can be passed to a parameter such as
`input_image`, `reference_image`, `input_audio`, or `input_video`.

When a generation tool waits for completion, its returned media is also saved
under `output` with a unique filename. The structured result contains both
`saved_path` and `output_filename`. Fire-and-forget submissions do not save
anything locally.

## Workflow discovery

The `workflows` subdirectory contains API-format JSON exported from ComfyUI. Each
`.json` filename becomes its `workflow_id`; for example,
`portrait_edit.json` is discovered as `portrait_edit`.

Run `workflow_list()` after adding a workflow. Each entry contains only
`workflow_id`, `description`, and `media_type`. Call `workflow_get_options()`
for parameters and configuration, or `workflow_get()` for workflow JSON.
The package intentionally ships without example workflows.

Workflows can also declare ordered execution-time transforms for seed-node
compatibility and MiniMax reference-media lists. See
[workflow transforms](workflow-transforms.md) for configuration, media options,
and `workflow_preview` examples.

`workflow_get_options(workflow_id)` includes a `bindings` list for each
parameter, with the exact `node_id` and `input_name` of every bound input.
Repeated occurrences within the same input appear once; bindings across
multiple nodes are all listed. This includes embedded placeholders, so agents
can inspect existing mappings without loading the entire workflow JSON.

## Workflow parameters

`workflow_parameterize()` edits a copied workflow export and replaces node input values
with quoted placeholders:

- `PARAM_NAME` or `PARAM_STR_NAME` for strings
- `PARAM_INT_NAME` for integers
- `PARAM_FLOAT_NAME` or `PARAM_FLT_NAME` for numbers
- `PARAM_BOOL_NAME` for booleans

For example:

```json
{
  "scheduler": "PARAM_SCHEDULER",
  "steps": "PARAM_INT_STEPS",
  "prompt": "PARAM_PROMPT"
}
```

The renderer emits the final JSON type, so `steps` becomes a JSON number rather
than a string. `PARAM_INT_SEED` may be generated when no explicit seed is
provided.

## YAML validation

YAML configuration is retained because some ComfyUI node constraints become
known only after a rejected workflow run. Recording those constraints avoids
repeating known-invalid submissions.

Each workflow may have
`<ComfyWorkflowMcp__Directory>/workflows/<workflow-id>.yaml`, beside its JSON:

```yaml
description: Generate detailed illustrations from a text prompt.
media_type: image
wait_for_completion: true
defaults:
  steps: 20
  scheduler: normal
allowed_values:
  scheduler: [normal, karras, exponential]
  sampler: [euler, dpmpp_2m]
constraints:
  steps:
    minimum: 1
    maximum: 100
  prompt:
    pattern: ".+"
```

Values supplied in a generation call override YAML defaults. Values outside a
known `allowed_values` list or configured constraint are rejected before
submission to ComfyUI.

`workflow_configure` validates the complete merged configuration before writing
YAML. Defaults must be convertible to the parameter's scalar type (or match a
reference-list schema), contain no `PARAM_*` placeholders, and satisfy the final
allowed values and constraints. Constraint rules must have valid types, numeric
bounds must be consistent, and regular expressions must compile. Invalid updates
leave both workflow JSON and YAML unchanged. Defaults and rules can be changed
together in one call.

The `required` field returned by `workflow_get_options` means the caller must
supply a value. A scalar with a default, or an integer `seed` with automatic
generation, is not required from the caller. Setting `constraints.required: false`
on another scalar requires a default; the server rejects that setting otherwise.
Scalar placeholders cannot be silently omitted from an executable workflow.
Reference lists default to empty arrays unless explicitly required and no default
is configured. Defaults satisfy `required: true` too.
Automatic seeds honor configured bounds and allowed values. Configuration is
rejected if an automatic fallback cannot produce an allowed nonnegative seed;
set an explicit default to use a negative sentinel. Optional reference lists
must permit their empty fallback or provide another default.

`description` is optional workflow metadata. `workflow_list()` returns it in
the workflow's `description` field, and `workflow_get_options()` includes it
alongside the other YAML-backed options. Without a nonblank description, the
catalog keeps its generated description (`Execute the '<workflow-id>' workflow.`).
Use YAML's `description: |` syntax for multiple lines.

You can edit the YAML directly or call
`workflow_configure(workflow_id: "portrait", description: "Generate portraits from text.")`.
Omitting `description` in `workflow_configure` preserves its current value; an empty
string removes it. Descriptions are metadata and are not workflow parameters.

Generation holds runs by default; `wait_for_completion` does not bypass the held
queue. The workflow setting is captured for playback, overridden by an explicit
generation argument, and defaults to `true`. `batch_play` can override the stored
settings for that playback. Waiting returns media; submit-only playback returns
ComfyUI prompt IDs without saving output locally. `enqueue: false` is an explicit
immediate-generation bypass.

Call `workflow_get_options(workflow_id)` to retrieve this YAML-backed data as
structured JSON together with the parameters discovered in the workflow JSON.

There are no family or model configuration layers. YAML is workflow-focused and
exists to capture validated parameters, defaults, and media type.

## Default workflows

`workflow_set_default(workflow_id: "portrait", media_type: "image")`
persists one default workflow for each media type in `settings.yaml`:

```yaml
default_workflows:
  image: portrait
  audio: song
  video: text_to_video
```

`generate_image`, `generate_audio`, and `generate_video` use the relevant
default when `workflow_id` is omitted. If no matching default exists,
`workflow_id` is required and the generation tool recommends `workflow_set_default`.

## Workflow configuration tools

`setup()` takes no arguments and is read-only. It returns paths, default
workflows, the workflow count, and a short guide. Mutations use focused tools:

- `workflow_configure()` merges workflow YAML settings.
- `workflow_parameterize()` adds or changes parameter bindings in workflow JSON.
- `workflow_unparameterize()` restores current defaults as fixed inputs.
- `workflow_set_default()` selects a default workflow for a media type.

Workflow exports are editable copies. The tools edit these files directly.

### Discovering parameters in workflow JSON

Call `setup()` for an embedded parameterization guide and
example. Use `workflow_list()` to discover IDs, then call
`workflow_get(workflow_id)` to retrieve the JSON; `workflow_id` is mandatory.
In an API-format ComfyUI export, the root object is keyed by node ID.
Each node's `inputs` object contains either literal values or connections to
other nodes. Connections are arrays such as `["105:104", 0]`; parameterize the
literal at its source node rather than replacing the connection.

Useful input names commonly include `cfg`, `steps`, `width`, `height`, `prompt`,
`negative`, and `negative_prompt`. Names vary by custom node, so inspect all
inputs and use their exact node ID and input key. Call
`workflow_parameterize` with:

```json
{
  "workflow_id": "example_workflow",
  "parameters": {
    "prompt": { "node_id": "6", "input_name": "text", "type": "string" },
    "cfg": { "node_id": "3", "input_name": "cfg", "type": "number" },
    "steps": { "node_id": "3", "input_name": "steps", "type": "integer" }
  }
}
```

The parameter-mapping schema requires `node_id`, `input_name`, and `type` for
each entry. The type is a string enum: `string`, `integer`, `number`, or
`boolean`; numeric enum values and unknown type names are rejected before
the workflow is changed. `workflow_parameterize`
replaces the selected literal inputs with typed `PARAM_*` placeholders in the
workflow copy. Follow it with `workflow_configure` to record defaults and known
validation rules, then inspect the result with `workflow_get_options()`.

Parameterization is additive. A call may add only `prompt`, followed later by a
call that adds only `steps`; the existing prompt placeholder remains in the
workflow JSON. A call changes only the explicitly targeted node inputs, so
previous mappings do not need to be repeated.

### Incremental YAML configuration

`workflow_configure` is additive too. Send only new or changed entries in
`defaults`, `allowed_values`, and `constraints`. Omitted entries and empty
objects preserve existing values. A supplied default replaces that parameter's
default; an allowed-values array replaces that parameter's entire list.
Constraint objects merge by rule, so changing `maximum` preserves an existing
`minimum`.

For example, after setting prompt and aspect-ratio defaults in one turn, a
later turn can call `workflow_configure` to add CFG without repeating those
values:

```json
{
  "workflow_id": "portrait",
  "defaults": { "cfg": 7.5 },
  "constraints": { "cfg": { "minimum": 1, "maximum": 20 } }
}
```

Use `null` to remove a specific entry. For example,
`"defaults": { "cfg": null }` removes only the CFG default, retaining its
placeholder. `"allowed_values": { "cfg": null }` removes its allowed-values
list. `"constraints": { "cfg": { "maximum": null } }` removes only the
maximum, whereas `"constraints": { "cfg": null }` removes all CFG constraints.

### Remove parameterization and restore a default

To make a parameter a fixed workflow input again, call
`workflow_unparameterize` with:

```json
{
  "workflow_id": "portrait",
  "parameter_names": ["cfg"]
}
```

This replaces every CFG binding with its **current YAML default**, preserving
the input's type, and removes CFG from `defaults`, `allowed_values`, and
`constraints`. Other parameters and their configuration remain unchanged.
Embedded placeholders are replaced within their strings while preserving the
surrounding text. The restored value is the current default, not the original
value that existed before parameterization.

Each selected parameter must exist and have a scalar default that matches its
type and configured validation rules. Defaults containing parameter
placeholders are rejected. If a default is missing, set it with
`workflow_configure` first. All selected parameters are validated before either
file is written, so an unknown parameter or invalid default leaves both files
unchanged. Inspect the result with `workflow_get()` and `workflow_get_options()`.

### Mutation receipts

Workflow edits return `success`, `workflow_id`, and only the change lists
that are nonempty. For example, adding a CFG default returns:

```json
{
  "success": true,
  "workflow_id": "portrait",
  "defaults_updated": ["cfg"]
}
```

Receipts can include `defaults_updated`, `defaults_removed`,
`allowed_values_updated`, `allowed_values_removed`, `constraints_updated`,
`constraints_removed`, `fields_updated`, and `fields_removed`. Constraint
lists identify affected parameters. Binding edits report `parameters_updated`
and any `parameters_removed`; restoring literals reports `parameters_restored`.
An unchanged update returns only `success` and `workflow_id`.
`workflow_set_default` returns the media type and `default_updated` boolean.
Failures return a stable error code, a message, and one recovery step:

```json
{
  "success": false,
  "error": "missing_default",
  "message": "Parameter 'cfg' requires a YAML default.",
  "parameter": "cfg",
  "next_step": "Set defaults.cfg with workflow_configure."
}
```

The same error shape is used by read, mutation, generation, and asset tools.
Errors set MCP's `isError` flag and include this object in both structured
content and JSON text, including input-binding failures before a tool runs.
The `parameter` field and `allowed_values` list appear when applicable.
Common codes include `workflow_not_found`, `unknown_parameter`,
`missing_default`, `invalid_default`, `invalid_value`, and `invalid_arguments`.
Follow `next_step` to correct the request instead of retrying unchanged.

ComfyUI submission failures use `workflow_rejected` and preserve the HTTP
`status_code`, `comfy_error`, and original `node_errors`. A flattened
`validation_errors` array contains each error's `node_id`, `class_type`,
`input_name` when provided, `type`, `message`, `details`, and `extra_info`.
Both structured content and JSON text contain the same error object. Non-JSON
HTTP failures retain their `response_body` instead of being classified as bad
workflow configuration.

If ComfyUI returns a `prompt_id` alongside branch errors, the failure preserves
it and sets `may_have_queued: true`. Inspect or cancel that job before submitting
again: other output branches may already have been accepted. The MCP server
does not automatically retry submissions.

These tools replace the former mutation operations on `setup()`. Refresh the
MCP connection to load the new schemas; migrate old calls to the named tools.
