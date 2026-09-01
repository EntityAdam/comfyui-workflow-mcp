# Changelog

## Unreleased

### Changed

- `setup()` now teaches clients how to parse ComfyUI API workflow JSON,
  discover common configurable inputs, and make additive
  `parameterize_workflow` calls without repeating existing bindings.
- Consolidated workflow, YAML, settings, and input paths under the single
  `ComfyWorkflowMcp__Directory` setting. `settings.yaml` lives at the root;
  workflow JSON and YAML share `workflows`; uploads are limited to `input`.
- Renamed the project, package, executable, namespaces, CI/CD identifiers, and
  configuration prefix to ComfyUI Workflow MCP.
- Generation tools now select wait or fire-and-forget execution with
  `wait_for_completion`; the backing workflow execution paths are no longer
  exposed as MCP tools.
- Local media uploads default to a dedicated `input` directory beside the
  executable instead of the process working directory.
- README, configuration documentation, architecture notes, and how-to guides
  now describe the simplified workflow and queue tool surface.
- Reduced the MCP surface to setup, workflow discovery, image/audio/video
  generation, media upload, cancellation, and queue controls.
- Workflow execution now has two stateless modes: wait through WebSocket and
  history to return the first requested output, or submit to ComfyUI and return
  immediately.
- Configuration is workflow-scoped YAML with defaults, allowed values, numeric
  bounds, string patterns, media type, and setup-managed media defaults.

### Added

- Workflow YAML can define `wait_for_completion`; an explicit generation-tool
  argument overrides it, and the fallback remains `true`.
- Completed `generate_image()` calls save a uniquely named reusable copy under
  the configured `input` directory and report its path and filename.
- `workflow_get(workflow_id)` gives clients read-only access to one parsed
  API-format workflow JSON; the workflow ID is mandatory.
- `workflow_get_options(workflow_id)` exposes YAML-backed defaults, allowed
  values, constraints, and discovered workflow parameters as structured data.

### Removed

- Bundled workflow JSON and YAML configuration.
- Model families and random family model selection.
- Durable generation and asset storage, retention, previews, regeneration, and
  publishing.

## 0.0.1-preview4 - 2026-07-27

### Added

- A complete MCP client configuration guide with examples for remote ComfyUI,
  persistent asset storage, retention, local media, and publishing.

### Fixed

- Successful generations now recover their prompt-specific outputs from
  ComfyUI history when a WebSocket output event is missed, arrives out of
  order, or is omitted for a cached node.
- Generations remain eligible for reconnect recovery when terminal history is
  not yet available instead of being finalized with zero assets.

## 0.0.1-preview3 - 2026-07-27

### Added

- Agent-first quickstart and focused media how-to guides.
- A bundled image-specific Real-ESRGAN upscaling workflow.
- Durable SQLite generation and asset records with explicit lifecycle states.
- Generation inspection through `generation_show()`.
- Event-driven completion through ComfyUI's plain WebSocket API, with targeted
  history recovery only for unfinished prompts submitted by this server.
- A preview3 implementation roadmap for event-driven completion and targeted
  recovery.

### Changed

- Detailed source, configuration, and release instructions now live under
  `docs/`.
- The bundled ACE-Step workflow accepts tool-supplied lyrics.
- CI runs documentation validation without building packages for docs-only
  changes, and exposes one stable required gate.
- Release jobs require a commit on `main` and grant publishing permissions only
  to the production job.
- Asset retention is count-based and defaults to the 128 newest concrete
  outputs; active generation records are never evicted.
- `image_show()` marks its generation viewed only after successfully returning
  media to the MCP client.
- Asset TTL configuration and expiration metadata have been removed.
- Generation tools return immediately after ComfyUI accepts and the server
  durably records a prompt.

## 0.0.1-preview2 - 2026-07-26

### Added

- NuGet `McpServer` packaging for version-pinned execution with `dnx`.
- Official MCP Registry metadata in `.mcp/server.json`.
- Cross-platform self-contained release archives.
- CI and release workflows that validate the installed package.

### Changed

- Bundled workflow and configuration defaults resolve relative to the installed
  executable rather than the caller's working directory.

## 0.1.0-rc.1 - 2026-07-26

### Added

- Streamable HTTP and stdio Model Context Protocol transports.
- Workflow discovery, typed parameter rendering, and hierarchical YAML defaults.
- Image and audio generation tools with family-aware model selection.
- Generated-asset registry, image previews, regeneration, and publishing.
- Environment-based configuration through standard .NET hierarchical keys.
- Polly-backed HTTP resilience for transient ComfyUI read failures.

### Changed

- Configuration environment variables use the `ComfyWorkflowMcp__PropertyName` convention.
- ComfyUI and MCP server defaults use `localhost`.
- Publishing now enforces `max_bytes` and rejects oversized source assets.

### Fixed

- MCP tools are listed deterministically.
- Queue prompt matching uses structured prompt IDs instead of JSON substring searches.
- Cancelled requests propagate cancellation instead of returning ordinary tool errors.
- Publishing uses atomic file replacement and serialized manifest updates.
