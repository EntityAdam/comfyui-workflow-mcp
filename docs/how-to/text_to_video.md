# Generate video from text

Add and configure an API-format video workflow during setup, then ask your
agent:

> Generate a short video of a paper boat floating through a miniature city
> during a rainstorm.

The agent should:

1. Use the configured default video workflow, or call `workflow_list()` to
   choose a workflow ID.
2. Call `generate_video()` with the prompt and the workflow's required
   parameters.
3. Let the default completion mode wait and return the first video output to
   the client.

Video generation can be long-running. Set `wait_for_completion` to `false` to
submit without waiting, then use `comfy_queue_list()` to inspect the ComfyUI
queue.
