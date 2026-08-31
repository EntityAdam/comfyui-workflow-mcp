# Edit an image

Attach an image and ask your agent:

> Upload this image to ComfyUI, use my image-edit workflow to replace the
> background with a quiet snowy forest, and show me the result.

The agent should:

1. Call `comfy_media_upload()` with the attachment's accessible `file_path`.
2. Call `workflow_list()` if no edit workflow ID or image default is known.
3. Call `generate_image()` with the selected `workflow_id`, the returned
   `workflow_value`, and the edit prompt.
4. Let the default completion mode wait and return the first image output to
   the client.

If the workflow accepts a reference image, upload that file separately and pass
its `workflow_value` to the matching parameter.

The attachment must be under `<ComfyWorkflowMcp__Directory>/input`. See
[Configuration](../configuration.md#input-media).
