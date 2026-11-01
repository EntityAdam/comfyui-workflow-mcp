# Held batch queue

Generation adds runs to a local held queue by default. It does **not** submit a
ComfyUI job until the user asks the agent to play the batch. Agents must not call
`batch_play` automatically after adding runs.

For example, add two runs with `generate_image`:

```json
{"workflow_id":"portrait","prompt":"A portrait in morning light"}
```

```json
{"workflow_id":"portrait","prompt":"A portrait in evening light"}
```

Each call returns `queued: true`, `submitted: false`, an `entry_id`, and its held
queue position. Check the pending count with `batch_count()`. When the user says
“play the batch,” call `batch_play()`. No workflow YAML changes are needed to
adopt this default.
Refresh the MCP connection to load the new tool schemas.

## Tools and completion modes

- `batch_count()` returns only the number of pending held runs, such as
  `{ "count": 3 }`. It does not return an inventory or playback history.
- `batch_remove(entry_id)` removes one pending or failed held run.
- `batch_clear()` removes pending and failed held runs.
- `batch_play(wait_for_completion)` plays a snapshot of the currently pending
  runs in their original order. New runs added during playback remain held for
  the next explicit Play.

Omitting `wait_for_completion` on Play honors each run's stored completion
setting. The setting comes from its generation argument, then workflow YAML,
then `true`. Setting Play's override to `false` submits the selected runs in order
and returns prompt IDs without waiting for media. Setting it to `true` waits for
each run, saves output locally, and returns the requested media. A waiting batch
can be long-running; use the submit-only override when a client cannot keep the
tool call open for the full generation time.

Setting `wait_for_completion: false` on a generation call **still holds the run**;
it records how that run should execute when played. To bypass the held queue for
an explicitly requested immediate run, use `enqueue: false`. Agents should only
use that bypass when the user has requested immediate execution.

The held queue is distinct from ComfyUI's queue. `comfy_queue_list`,
`comfy_queue_clear`, and `comfy_job_cancel` operate on jobs already submitted to
ComfyUI. Batch removal and clearing never cancel those jobs or interrupt active
playback. Playback records are retained so submission IDs are not lost.
Use the `entry_id` from a generation or playback receipt to remove a held run.
Playback receipts contain per-run results; interrupted playback records remain
available in the saved queue file. Selected, failed, and submitted runs are
excluded from the pending count.

## Snapshots and persistence

The queue is stored in `<ComfyWorkflowMcp__Directory>/batch/queue.json` and
survives server restarts. There is no background worker or automatic playback on
startup. Queueing runs and checking their count work while ComfyUI is offline;
uploads and playback still require it.

Each run captures its workflow graph, resolved defaults/options, seed, date
placeholders, transform definitions, completion setting, media type, and ComfyUI
URL when added. Subsequent template or YAML edits do not change held runs.
Remove and enqueue a replacement to change a held run. Transform node-schema
inspection and dynamic media-loader expansion happen at Play, against the
installed ComfyUI nodes. Media filenames must still exist in that server's input
storage at playback time; input media bytes are not copied into the queue.

Queue writes use a separate lock and an atomic file replacement. A playback
lock prevents concurrent Play calls against the same directory, including from
separate server instances. If another process is briefly editing the queue,
`batch_busy` means that queue operation can be retried without submitting a run.
An invalid queue file is reported and left unchanged.

## Errors and interrupted playback

Playback stops at its first error. Earlier results remain recorded and unstarted
runs return to pending. Nothing resumes until another explicit Play. Failed runs
are not retried automatically; remove and enqueue a corrected replacement.

Local tracking statuses are:

| Status | Meaning |
|---|---|
| `pending` | Held until the next explicit Play |
| `scheduled` | Selected for the active batch, not dispatched yet |
| `submitting` | Submission is being attempted |
| `running` | Submission ID saved; the waiting tool call is monitoring it |
| `submitted` | ComfyUI accepted the job; completion is not tracked locally |
| `completed` | Requested output retrieved and saved |
| `failed` | Failed before submission or explicitly rejected without acceptance |
| `submitted_with_errors` | A prompt ID is known, but submission or waiting reported an error |
| `uncertain` | Submission may have reached ComfyUI, but no prompt ID was saved |

Statuses are local bookkeeping, not a live view of GPU execution. For example,
a submit-only entry remains `submitted` even after ComfyUI completes it.

The prompt ID is persisted immediately after a successful submission, before
waiting for output. When Play detects a previous interrupted process, unstarted
scheduled entries return to pending. An interrupted `submitting` entry becomes
`uncertain`; an interrupted waiting entry with a known prompt ID becomes
`submitted`. Neither is submitted again automatically. Check ComfyUI's queue or
history before creating replacements for uncertain or already-submitted work.

Playback returns a batch receipt with per-run results. A stopped batch uses
`batch_stopped` with MCP's error flag and preserves the underlying structured
ComfyUI errors in the affected run. Cancelling a playback request does not cancel
an already accepted ComfyUI job; use `comfy_job_cancel` explicitly if needed.
