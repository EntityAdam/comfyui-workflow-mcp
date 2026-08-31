# Configuration

## Environment

- `ComfyMcp__BaseUrl`: ComfyUI server URL.
- `ComfyMcp__ServerUrl`: MCP HTTP server URL.
- `ComfyMcp__WorkflowDirectory`: directory containing copied ComfyUI API-format JSON workflows.
- `ComfyMcp__ConfigurationDirectory`: directory containing `settings.yaml` and workflow YAML.
- `ComfyMcp__InputRoot`: local path boundary for media uploads.

## Settings

`configuration/settings.yaml` persists defaults and a setup-selected workflow directory:

```yaml
workflow_directory: /absolute/path/to/workflows
default_workflows:
  image: image_krea2_turbo_t2i
  audio: audio_ace_step1_5_xl_turbo
  video: video_ltx2_3_t2v
```

An explicit `workflow_id` always overrides the corresponding default. When neither exists, generation tools recommend running `setup()`.

## Workflow validation

Only workflow-scoped YAML exists. Resolution order is workflow YAML followed by values supplied in the tool call.

Supported keys are `media_type`, `defaults`, `allowed_values`, and `constraints`. Constraints support `minimum`, `maximum`, and `pattern`.

Use setup operations to inspect status, change the workflow directory, parameterize copied JSON, write YAML configuration, or select a default workflow. JSON and YAML are edited directly; setup does not create backups or versions.
