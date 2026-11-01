# Generate an image from text

Ask your agent:

> Generate a cinematic photograph of a rain-soaked street market at night and
> show me the result.

The agent should:

1. Use the configured default image workflow, or call `workflow_list()` to
   choose a workflow ID.
2. Call `generate_image()` with the prompt and any required workflow
   parameters.
3. Check the held run count with `batch_count()`.
4. When the user explicitly asks to play the batch, call `batch_play()` to run it
   and return the first image output.

If there is no default and no `workflow_id` is provided, `generate_image()`
recommends `workflow_set_default()`. The workflow tools can parameterize a copied ComfyUI workflow,
record YAML validation constraints, and save it as the image default.
