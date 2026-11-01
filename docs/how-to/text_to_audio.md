# Generate audio from text

Add and configure an API-format audio workflow during setup, then ask your
agent:

> Use ComfyUI to make a 30-second upbeat synth-pop song at 120 BPM with a short
> chorus about exploring a neon city.

The agent should:

1. Call `workflow_list()` if no audio workflow ID or audio default is known.
2. Call `generate_audio()` with the selected `workflow_id` and the parameters
   exposed by that workflow.
3. Check the held run count with `batch_count()`.
4. When the user explicitly asks to play the batch, call `batch_play()` to run it
   and return the first audio output.

For long-running audio, use `batch_play(wait_for_completion: false)` when the
user asks to play without waiting for media. `comfy_queue_list()` can then inspect
the submitted jobs. Generation calls hold runs by default, including when their
`wait_for_completion` argument is false.
