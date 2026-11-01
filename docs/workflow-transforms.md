# Workflow transforms

Transforms build an execution graph from a saved API-format workflow. Preparation
renders scalar `PARAM_*` placeholders, applies enabled transforms in YAML order,
and checks connections before submission. It never writes the prepared graph
back to the template. Both completion modes use the same preparation service.

Configure transforms in the workflow's YAML or through `workflow_configure`:

```yaml
transforms:
  - id: seed_compatibility
    kind: replace_negative_rgthree_seed
  - id: references
    kind: minimax_references
    node_id: "136"
```

Each definition has a unique, nonempty `id`, a supported `kind`, and an optional
boolean `enabled` (default `true`). `node_id` is required for `minimax_references`.
Node IDs are exact strings, including colons and leading zeros; quote them in
YAML. Only one enabled reference transform is supported per workflow.

`workflow_configure` replaces the **entire ordered transform list** when
`transforms` is supplied. Omission or `null` preserves it; `[]` removes all
transforms. Other configuration sections keep their existing merge behavior.
The change receipt includes `transforms_updated: true` when the list changes.
If disabling a reference transform, also remove any reference-list defaults or
constraints that are no longer applicable, using null entries in the same call.
This also applies to allowed-value lists. The complete merged configuration is
validated before saving; disabling a transform while leaving its options behind
is rejected without changing YAML.

`workflow_get_options` returns the transform definitions and the schemas of
their public options. Ordinary scalar parameter bindings work as before.
Fragments under a `nodes` directory are authoring examples, not loaded at runtime;
the supported adapters implement their connection contracts in code.

## Negative rgthree seeds

`replace_negative_rgthree_seed` matches every `Seed (rgthree)` node whose
rendered `seed` is exactly `-1`. It replaces the class and inputs with
`SeedNode` and `{ "seed": -1 }`, retaining the original node ID and downstream
connections. Metadata is preserved, with the title updated to `Seed`.
Nonnegative seeds and other node classes are unchanged. Omitted integer `seed`
parameters still receive the server's existing random nonnegative value.

The replacement must be installed in ComfyUI, accept an integer seed of `-1`,
and expose an `INT` at output 0. The server checks `/object_info/SeedNode` before
replacement. Configured seed constraints must also permit `-1` if supplied.
There is no KitchenAttention transform.

## Ordered MiniMax references

`minimax_references` targets an existing `MiniMaxH3ReferenceToVideo` node. It
owns that node's four reference groups and replaces their wiring on every run.
It preserves prompt, dimensions, frame count, CLIP/VAE connections, and output
connections. It does not automatically convert an image-to-video node or choose
model weights. Use a reference-capable checkpoint and wire the target's required
`clip` input. Wire `vae` and `audio_vae` when reference latent encoding is needed.

Pass media lists inside generation `options`:

```json
{
  "reference_images": ["character.png", "location.png"],
  "reference_videos": [
    { "file": "motion.mp4", "include_audio": true },
    { "file": "silent.mp4" }
  ],
  "reference_audios": ["voice.wav"]
}
```

Names must already exist in ComfyUI's input storage. Use `comfy_media_upload`
first for staged assets and pass its returned `workflow_value`, including any
subfolder. Preparation does not upload media. Lists can also be YAML defaults.
An omitted list defaults to `[]` unless configured as required without a default;
a supplied list replaces its whole default.
Empty lists clear the corresponding reference sockets. Explicit nulls and
malformed entries are rejected. These option names cannot also be scalar
`PARAM_*` placeholders.

Supported limits are 9 images, 3 videos, and 3 standalone audio clips; the
installed node's slot limits are also checked and may be smaller. The adapter
validates the installed V3 autogrow prefixes and media types and checks the
loader nodes it needs. Missing or incompatible nodes fail before submission.
Definitions are reused within a preparation call, not cached across calls.

Each image creates a `LoadImage`; each audio clip creates a `LoadAudio`. Each
video creates `LoadVideo` → `GetVideoComponents`. Frames connect to
`ref_videos.ref_video_N`; when `include_audio` is true, audio output 1 connects
to `ref_video_audios.ref_video_audio_N` with the **same N**, even when preceding
videos omit audio. `include_audio` defaults to false.

List order determines socket order. Socket indices start at 0, while prompt
tags start at 1: `<Picture 1>`, `<Video 1>`, `<Audio 1>`. Video soundtracks receive
audio tags before standalone audio clips; account for included soundtracks when
numbering standalone audio in a prompt. The server does not rewrite prompt tags.
The node defines these ordering rules in
[ComfyUI's MiniMax implementation](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_minimax_h3.py).

Generated node IDs are deterministic and avoid existing IDs. Generated loaders
carry a transform ownership marker under `_meta`. Rebuilding removes only that
transform's generated loaders; user-created loaders remain intact. Preparation
rejects removal when an unrelated node consumes a generated loader. Dangling
connections, negative output slots, and cycles are rejected for workflows with
configured transforms. ComfyUI remains responsible for full workflow validation,
media decoding, checkpoint compatibility, and runtime execution.

## Preview and execution receipts

```json
{
  "workflow_id": "reference_video",
  "options": {
    "prompt": "<Picture 1> walks through the scene",
    "reference_images": ["character.png"]
  }
}
```

Pass these arguments to `workflow_preview`. It returns `workflow` (the rendered
graph) and `transforms_applied` (operations with transform and node IDs), without
submitting a job. Enabled transforms may require a running ComfyUI for schema
inspection. Preview and generation are separate preparations; an omitted random
seed can differ between them, so supply an explicit seed for comparison.

Generation holds a snapshot by default; installed schemas are checked and
transforms are applied when the user asks to play the batch. Playback results
(and explicitly immediate generation) include `transforms_applied` alongside
media or prompt IDs. See [held batches](batch-queue.md). Errors use the existing
structured error format:
`invalid_transform`, `node_unavailable`, or `invalid_value` as applicable. No
automatic retry or graph repair follows a ComfyUI rejection.
