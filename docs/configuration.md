# Configuration

## Environment variables

Configuration is bound from the `ComfyWorkflowMcp` section. ASP.NET environment
variables replace `:` with `__`:

- `ComfyWorkflowMcp__BaseUrl` — ComfyUI URL; default `http://localhost:8188`
- `ComfyWorkflowMcp__ServerUrl` — public MCP server URL; default
  `http://localhost:9001`
- `ComfyWorkflowMcp__Directory` — root for `settings.yaml`, workflows, workflow
YAML, staged assets, and generated output; default is the directory containing the executable

These directories are not bundled. The server can start while ComfyUI is
offline, but uploads, queue operations, and generation require ComfyUI.

## Media assets

`comfy_media_upload` accepts an image, audio file, or video and sends it through
ComfyUI's upload endpoint. A local file must be inside
`<ComfyWorkflowMcp__Directory>/assets`:

```json
{
  "filename": "source.mp4"
}
```

Use `asset_stage()` to save base64/data-URL client media under `assets`.
`asset_list()` enumerates staged assets, and `asset_exists()` validates an asset
filename before it is uploaded.

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

Run `workflow_list()` after adding a workflow. The
package intentionally ships without example workflows.

## Workflow parameters

Setup may edit a copied workflow export directly and replace node input values
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

`description` is optional workflow metadata. `workflow_list()` returns it in
the workflow's `description` field, and `workflow_get_options()` includes it
alongside the other YAML-backed options. Without a nonblank description, the
catalog keeps its generated description (`Execute the '<workflow-id>' workflow.`).
Use YAML's `description: |` syntax for multiple lines.

You can edit the YAML directly or call
`setup(operation: "configure_workflow", workflow_id: "portrait", description: "Generate portraits from text.")`.
Omitting `description` during setup preserves its current value; an empty
string removes it. Descriptions are metadata and are not workflow parameters.

`wait_for_completion` controls the workflow's generation default. An explicit
generation-tool argument overrides it; when neither is provided, the server
waits for completion. Fire-and-forget workflows return their ComfyUI prompt ID
and do not save output media locally.

Call `workflow_get_options(workflow_id)` to retrieve this YAML-backed data as
structured JSON together with the parameters discovered in the workflow JSON.

There are no family or model configuration layers. YAML is workflow-focused and
exists to capture validated parameters, defaults, and media type.

## Default workflows

Setup can persist one default workflow for each media type in `settings.yaml`:

```yaml
default_workflows:
  image: portrait
  audio: song
  video: text_to_video
```

`generate_image`, `generate_audio`, and `generate_video` use the relevant
default when `workflow_id` is omitted. If no matching default exists,
`workflow_id` is required and the generation tool recommends running `setup()`.

## Setup responsibilities

`setup()` is allowed to:

- inspect current paths, defaults, and workflows;
- create or update workflow YAML configuration;
- edit copied ComfyUI API-format JSON workflows in place;
- parameterize node inputs; and
- save default workflow selections.

Workflow exports are treated as editable copies. Setup does not need to retain
an untouched source version or create version history.

### Discovering parameters in workflow JSON

Call `setup(operation: "status")` for an embedded parameterization guide and
example. Use `workflow_list()` to discover IDs, then call
`workflow_get(workflow_id)` to retrieve the JSON; `workflow_id` is mandatory.
In an API-format ComfyUI export, the root object is keyed by node ID.
Each node's `inputs` object contains either literal values or connections to
other nodes. Connections are arrays such as `["105:104", 0]`; parameterize the
literal at its source node rather than replacing the connection.

Useful input names commonly include `cfg`, `steps`, `width`, `height`, `prompt`,
`negative`, and `negative_prompt`. Names vary by custom node, so inspect all
inputs and use their exact node ID and input key:

```json
{
  "operation": "parameterize_workflow",
  "workflow_id": "example_workflow",
  "parameters": {
    "prompt": { "node_id": "6", "input_name": "text", "type": "string" },
    "cfg": { "node_id": "3", "input_name": "cfg", "type": "number" },
    "steps": { "node_id": "3", "input_name": "steps", "type": "integer" }
  }
}
```

The supported types are `string`, `integer`, `number`, and `boolean`. Setup
replaces the selected literal inputs with typed `PARAM_*` placeholders in the
workflow copy. Follow it with `configure_workflow` to record defaults and known
validation rules, then inspect the result with `workflow_get_options()`.

Parameterization is additive. A call may add only `prompt`, followed later by a
call that adds only `steps`; the existing prompt placeholder remains in the
workflow JSON. A call changes only the explicitly targeted node inputs, so
previous mappings do not need to be repeated.

### Incremental YAML configuration

`configure_workflow` is additive too. Send only new or changed entries in
`defaults`, `allowed_values`, and `constraints`. Omitted entries and empty
objects preserve existing values. A supplied default replaces that parameter's
default; an allowed-values array replaces that parameter's entire list.
Constraint objects merge by rule, so changing `maximum` preserves an existing
`minimum`.

For example, after setting prompt and aspect-ratio defaults in one turn, a
later turn can add CFG without repeating those values:

```json
{
  "operation": "configure_workflow",
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

To make a parameter a fixed workflow input again, use:

```json
{
  "operation": "unparameterize_workflow",
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
`configure_workflow` first. All selected parameters are validated before either
file is written, so an unknown parameter or invalid default leaves both files
unchanged. Inspect the result with `workflow_get()` and `workflow_get_options()`.
