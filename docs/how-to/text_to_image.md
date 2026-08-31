# Generate an image from text

Ask your agent:

> Generate a cinematic photograph of a rain-soaked street market at night and
> show me the result.

The agent should:

1. Use the configured default image workflow, or call `workflow_list()` to
   choose a workflow ID.
2. Call `generate_image()` with the prompt and any required workflow
   parameters.
3. Let the default completion mode wait and return the first image output to
   the client.

If there is no default and no `workflow_id` is provided, `generate_image()`
recommends running `setup()`. Setup can parameterize a copied ComfyUI workflow,
record YAML validation constraints, and save it as the image default.
