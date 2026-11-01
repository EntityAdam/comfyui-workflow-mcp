# Upscale an image

This project does not bundle an upscaling workflow or model. Add a tested
API-format upscaling workflow during setup.

Attach an image and ask your agent:

> Upload this image, run my image-upscaling workflow, and show me the result.

The agent should:

1. Call `asset_list()` to find the existing asset, then call
   `comfy_media_upload()` with its filename. For new attachments, the client
   can supply bytes directly through `asset_stage()`; the agent must never
   generate or reproduce base64. If the client cannot supply bytes directly,
   place the file in `assets/` first.
2. Call `workflow_list()` if the workflow ID is not known.
3. Call `generate_image()` with the upscaling `workflow_id` and the returned
   `workflow_value` for the workflow's image input.
4. Review the held run and wait for the user to ask to play the batch.
5. Call `batch_play()` when requested and return the first completed image.

The required upscaler and custom nodes must already be installed in ComfyUI.
