# Image upscale

1. Upload the source with `comfy_media_upload(file_path)`.
2. Call `workflow_run()` with an image-upscale workflow and use the returned `workflow_value` for its media placeholder.
3. The first image output is returned directly.
