# Image editing

1. Upload each reference image with `comfy_media_upload(file_path)`.
2. Call `generate_image()` with an edit workflow and pass the returned `workflow_value` strings in `options`.
3. The first edited image is returned directly.
