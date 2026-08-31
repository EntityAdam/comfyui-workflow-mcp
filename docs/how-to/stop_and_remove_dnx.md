# Stop or remove a DNX-launched server

`dnx` executes `ComfyUi.WorkflowMcp` without permanently installing a .NET
tool. There is therefore no `dnx uninstall` command.

## Stop the server

When an MCP client launches the server over standard input/output, the server
should stop when the client disconnects or exits.

1. Disable or remove the `comfyui` entry from the MCP client's configuration.
2. Restart the MCP client so it closes the existing stdio connection.
3. If `dnx` is running in a terminal, press <kbd>Ctrl</kbd>+<kbd>C</kbd>.

To check for a process that did not exit:

```bash
pgrep -af 'ComfyUi.WorkflowMcp|dotnet dnx.*ComfyUi.WorkflowMcp'
```

On macOS, where `pgrep -a` does not show the full command, use:

```bash
ps -axo pid,command | grep -E 'ComfyUi\.WorkflowMcp|dotnet dnx.*ComfyUi\.WorkflowMcp'
```

Ignore the `grep` process itself. If a matching server remains, first request a
normal termination using the exact PID shown by the previous command:

```bash
kill -TERM <pid>
```

DNX does not create a background service or startup item. If the server returns
after termination or login, inspect the mechanism that launched it, such as an
MCP client, launch agent, scheduled task, or service manager.

## Remove it from the MCP client

Delete the server entry that invokes `dnx`, then restart the client. For
example, remove this complete object from `mcpServers` or `servers`:

```json
"comfyui": {
  "command": "dnx",
  "args": [
    "ComfyUi.WorkflowMcp@0.0.1-preview5",
    "--yes",
    "--",
    "--stdio"
  ]
}
```

This is sufficient to stop future DNX launches.

## Optionally remove cached package files

DNX may reuse NuGet's package caches on later runs. Cached files are not a
global tool installation and are safe to leave in place.

List the cache locations:

```bash
dotnet nuget locals global-packages --list
dotnet nuget locals http-cache --list
```

For targeted cleanup, open the reported global-packages directory and remove
only its `comfyui.workflowmcp` subdirectory after verifying the path. Do not
delete the entire global-packages directory.

To clear every cached NuGet package and HTTP response instead:

```bash
dotnet nuget locals global-packages --clear
dotnet nuget locals http-cache --clear
```

This broader operation affects all .NET projects and tools on the machine.
Packages required later will be downloaded again.
