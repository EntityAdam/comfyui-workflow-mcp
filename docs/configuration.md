# Configuration

## Environment variables

Configuration is bound from the `ComfyWorkflowMcp` section. ASP.NET environment
variables replace `:` with `__`:

- `ComfyWorkflowMcp__BaseUrl` — ComfyUI URL; default `http://localhost:8188`
- `ComfyWorkflowMcp__ServerUrl` — public MCP server URL; default
  `http://localhost:9001`
- `ComfyWorkflowMcp__WorkflowDirectory` — initial workflow JSON directory;
  default is a `workflows` directory beside the executable
- `ComfyWorkflowMcp__ConfigurationDirectory` — YAML configuration directory;
  default is a `configuration` directory beside the executable
- `ComfyWorkflowMcp__InputRoot` — root from which local media may be uploaded;
  default is an `input` directory beside the executable

These directories are not bundled. The server can start while ComfyUI is
offline, but uploads, queue operations, and generation require ComfyUI.

## Input media

`comfy_media_upload` accepts an image, audio file, or video and sends it through
ComfyUI's upload endpoint. A local file must be inside
`ComfyWorkflowMcp__InputRoot`:

```json
{
  "file_path": "/absolute/path/to/input/source.mp4"
}
```

The server does not scan the input root. It opens only the exact file supplied
to the tool. To use media from another directory, explicitly configure that
directory as `ComfyWorkflowMcp__InputRoot`.

The returned `workflow_value` can be passed to a parameter such as
`input_image`, `reference_image`, `input_audio`, or `input_video`.

## Workflow discovery

The workflow directory contains API-format JSON exported from ComfyUI. Each
`.json` filename becomes its `workflow_id`; for example,
`portrait_edit.json` is discovered as `portrait_edit`.

Run `workflow_list()` after changing the directory or adding a workflow. The
package intentionally ships without example workflows.

Setup can persist another workflow path in
`<configuration-directory>/settings.yaml`:

```yaml
workflow_directory: /absolute/path/to/workflow-json
```

The setup-selected path takes precedence over the initial environment setting.

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
`<configuration-directory>/workflows/<workflow-id>.yaml`:

```yaml
media_type: image
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
- change the workflow JSON directory;
- create or update workflow YAML configuration;
- edit copied ComfyUI API-format JSON workflows in place;
- parameterize node inputs; and
- save default workflow selections.

Workflow exports are treated as editable copies. Setup does not need to retain
an untouched source version or create version history.
