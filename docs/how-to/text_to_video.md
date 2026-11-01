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
3. Check the held run count with `batch_count()`.
4. When the user explicitly asks to play the batch, call `batch_play()` to run it
   and return the first video output.

Video generation can be long-running. When the user asks to play, use
`batch_play(wait_for_completion: false)` to submit without waiting. Then use
`comfy_queue_list()` to inspect the submitted jobs. Adding a run never starts it
automatically.
