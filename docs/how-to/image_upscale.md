# Upscale an image

This project does not bundle an upscaling workflow or model. Add a tested
API-format upscaling workflow during setup.

Attach an image and ask your agent:

> Upload this image, run my image-upscaling workflow, and show me the result.

The agent should:

1. Call `comfy_media_upload()` with the image's accessible `file_path`.
2. Call `workflow_list()` if the workflow ID is not known.
3. Call `generate_image()` with the upscaling `workflow_id` and the returned
   `workflow_value` for the workflow's image input.
4. Return the first completed image output to the client.

The required upscaler and custom nodes must already be installed in ComfyUI.
