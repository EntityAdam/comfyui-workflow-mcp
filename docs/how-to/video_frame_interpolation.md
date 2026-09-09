# Interpolate video frames

This project does not bundle an interpolation workflow. Add a tested API-format
workflow during setup.

Attach a video and ask your agent:

> Upload this video, run my frame-interpolation workflow with a multiplier of
> 2, and return the resulting video.

The agent should:

1. Call `asset_stage()` with the video's base64 data, then call
   `comfy_media_upload()` with its returned filename.
2. Call `workflow_list()` if the interpolation workflow ID is not known.
3. Call `generate_video()` with the selected `workflow_id`, the returned
   `workflow_value`, and the requested multiplier.
4. Return the first completed video output to the client.

Higher multipliers require more processing and produce larger outputs.
