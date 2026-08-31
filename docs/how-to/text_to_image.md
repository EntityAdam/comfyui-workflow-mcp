# Text to image

1. Call `workflow_list()` and choose an image workflow.
2. Call `generate_image(prompt, workflow_id, options)`.
3. The tool waits and returns the first image output directly.

To leave a long generation on ComfyUI, call `workflow_run_on_server(workflow_id, options)` instead.
