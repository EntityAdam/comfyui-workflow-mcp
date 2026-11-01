# Edit an image

Attach an image and ask your agent:

> Upload this image to ComfyUI, use my image-edit workflow to replace the
> background with a quiet snowy forest, and show me the result.

The agent should:

1. Call `asset_list()` to find the existing asset, then call
   `comfy_media_upload()` with its filename. For new attachments, the client
   can supply bytes directly through `asset_stage()`; the agent must never
   generate or reproduce base64. If the client cannot supply bytes directly,
   place the file in `assets/` first.
2. Call `workflow_list()` if no edit workflow ID or image default is known.
3. Call `generate_image()` with the selected `workflow_id`, the returned
   `workflow_value`, and the edit prompt.
4. Check the held run count with `batch_count()` and wait for the user to ask to play.
5. Call `batch_play()` when requested and return the first image output.

If the workflow accepts a reference image, upload that file separately and pass
its `workflow_value` to the matching parameter.

The attachment must be under `<ComfyWorkflowMcp__Directory>/assets`. See
[Configuration](../configuration.md#media-assets).
