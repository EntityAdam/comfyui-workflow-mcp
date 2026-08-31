# Generate audio from text

Add and configure an API-format audio workflow during setup, then ask your
agent:

> Use ComfyUI to make a 30-second upbeat synth-pop song at 120 BPM with a short
> chorus about exploring a neon city.

The agent should:

1. Call `workflow_list()` if no audio workflow ID or audio default is known.
2. Call `generate_audio()` with the selected `workflow_id` and the parameters
   exposed by that workflow.
3. Let the default completion mode wait and return the first audio output to
   the client.

For a long-running job that does not need an immediate result, set
`wait_for_completion` to `false`. The call returns after ComfyUI accepts the
job; `comfy_queue_list()` can then report whether it is running or pending.
