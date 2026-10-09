# Project Instructions

This is WinToolBridge, a Windows local bridge application built with C# and .NET 10. It uses a Windows Forms tray application and a local WebSocket server to expose system functionality to the WinTool frontend.

Follow the existing architecture and coding style. Prefer minimal, focused changes and avoid unrelated refactors.

## Scope

- These instructions apply to the WinToolBridge backend repository.
- When a task requires frontend changes, read and follow that project's `AGENTS.md` before editing its files.
- Do not modify the frontend's `AGENTS.md` as part of backend work unless explicitly requested.
- Keep frontend UI, translations, and request handling in the frontend project.

## Architecture

- Keep the application compatible with the Windows target framework declared in `WinToolBridge/WinToolBridge.csproj`.
- Preserve the existing tray application, single-instance behavior, and local bridge architecture unless the task requires changing them.
- `LocalBridgeServer` owns connection handling, protocol validation, command dispatch, acknowledgements, retries, and connection cleanup.
- Command handlers implement `ICommandHandler` and are registered in `CommandRegistry`.
- Reusable system queries and related functionality belong under `Services/`. Follow nearby handlers when deciding whether a small operation needs a separate service.
- Do not put command-specific system operations into the WebSocket transport layer.

## Project Structure

Important files and directories:

- `WinToolBridge/Program.cs` — application lifecycle and tray UI
- `WinToolBridge/LocalBridgeServer.cs` — local HTTP/WebSocket server and protocol
- `WinToolBridge/Commands/ICommandHandler.cs` — command interface
- `WinToolBridge/Commands/CommandRegistry.cs` — registration and dispatch
- `WinToolBridge/Commands/Handlers/` — command implementations
- `WinToolBridge/Services/` — system queries and reusable operations
- `WinToolBridge/Resources/` — application resources and localized strings
- `.github/workflows/release.yml` — release build and publishing workflow

Do not manually modify generated files or build output, including `bin/`, `obj/`, and `Resources/Strings.Designer.cs`.

## Commands and Protocol

- Review similar handlers before adding a command. Use the existing action naming convention, such as `SYS:...` and `NET:...`.
- Register new handlers in `CommandRegistry` and return results through the existing dispatch and response flow.
- When changing an action, parameter, result, or protocol field, inspect the corresponding frontend types and command wrappers. Keep both sides compatible within the requested scope.
- Preserve existing JSON property names, units, nullability, status values, and request correlation behavior unless the requested change requires an update.
- Validate required parameters and JSON value kinds before reading or using them. Treat all client input as untrusted.
- An ACK acknowledges message receipt; it does not mean the command completed. Keep result delivery and ACK retry handling separate.
- Keep retry and acknowledgement state scoped to the originating connection. A client must not acknowledge or cancel another connection's retries.
- Use the existing error response flow. Do not leave a correlated request waiting when a recoverable validation or command error can be returned.
- For long-running commands, review expected duration and the existing client deadline. Do not add arbitrary delays or change transport retry timing to accommodate command execution time.

## Local Server and System Operations

- Preserve loopback-only listening and the explicit origin allowlist. Do not broaden access merely to work around a connection failure.
- Preserve existing connection limits and their release paths, including failed upgrades and disconnected sessions.
- Opening applications, directories, or Windows settings must use backend-defined allowed targets or validated system enumeration results.
- Do not accept arbitrary executable paths, shell commands, command-line switches, or application package identities from the frontend.
- Prefer Windows APIs or a fixed executable path with `ProcessStartInfo`. Use `ArgumentList` for variable arguments where supported, and avoid shell command concatenation.
- Handle missing applications, unavailable devices, disconnected drives, and access failures without crashing the bridge.
- Do not install drivers or applications, change startup settings, or modify system configuration merely to make a test pass.

## Async Work and Resource Lifetime

- Pass the provided `CancellationToken` to operations that support cancellation and check it before starting new work or side effects.
- Cancellation is cooperative. Passing a token to `Task.Run` does not interrupt a synchronous Windows API once it is running.
- Keep connection-owned work tied to the connection lifetime. Clean up retries, timers, and pending background work on disconnect or shutdown.
- Observe exceptions from background tasks. Follow the existing error handling and logging flow rather than allowing unhandled failures.
- Use `using` or `finally` to release registry keys, process handles, native buffers, and other owned resources.
- Disposing a `Process` object releases its handle; it does not terminate the launched application.
- Make shutdown and disposal safe for repeated calls. Do not dispose shared resources while background work can still use them.
- Complete WebSocket close handling with a bounded wait and always release the socket, including failure paths.
- Do not add forced garbage collection as a routine memory fix. Investigate retained objects and native resources; a stable memory plateau alone does not establish a leak.

## Windows APIs and Enumeration

- Prefer existing providers and Windows API integrations over new dependencies or shell-based queries.
- Preserve relevant Windows and architecture compatibility, including the release targets declared in the workflow.
- For P/Invoke changes, check structure layout, native types, buffer sizes, error handling, and matching allocation/free operations.
- Verify what an API actually enumerates. A framework or driver update may expose virtual, filter, or otherwise unsuitable entries.
- Filter by capabilities and system properties where possible. Do not exclude devices only because their display names resemble a particular machine's unwanted entries.
- Keep units and calculations consistent with the existing result contract. Avoid silently substituting zero for unavailable data when the distinction matters to the caller.

## Application Resources

- Reuse `Resources/Strings.resx` and the corresponding `Strings.zh-TW.resx`, `Strings.zh-CN.resx`, and `Strings.ja.resx` for localized tray UI and application messages.
- Keep resource keys consistent when adding or changing localized messages. Do not build a separate localization mechanism.
- Keep application UI messages separate from protocol data and diagnostic errors. Do not move frontend wording or locale selection into backend handlers.

## Dependencies and Release Configuration

- Prefer the .NET libraries and dependencies already used by the project.
- Avoid adding or upgrading packages unless necessary for the task. Explain the reason for any dependency change.
- When changing the target framework or SDK, review the release workflow for consistency.
- Preserve existing publishing settings and release architectures unless explicitly requested otherwise.
- Do not publish releases or push release tags unless explicitly requested.

## Development and Validation

- Run checks appropriate to the change. For C# implementation changes, build the solution and distinguish existing failures from failures introduced by the task.
- Avoid overwriting output used by a running bridge. When necessary, build to a separate output directory:

```powershell
dotnet build WinToolBridge.sln --no-restore -p:OutputPath=bin/ReviewCheck/
```

- Use `--no-restore` when dependencies are already restored. Restore dependencies when required by the task or environment.
- Use focused tests for protocol, concurrency, validation, or resource-lifetime changes. Scale verification to the risk and avoid introducing a test framework for a trivial change.
- Use an isolated local server or direct handler tests when possible. Do not stop or replace the user's running bridge just to test a change.
- Do not automatically open desktop applications or modify system state during verification unless that interaction is part of the requested testing.
- Browser interaction testing is normally handled by the user. When backend changes also require frontend updates, follow the frontend's validation instructions.
- Report what changed, what was verified, and what still requires actual Windows or browser interaction. A successful build does not prove that a control panel opened or a native API worked on every machine.

## General Coding Rules

- Follow nearby C# formatting, naming, nullable annotations, and error handling patterns.
- Reuse existing interfaces, helpers, providers, and libraries. Avoid unnecessary abstractions.
- Preserve the user's uncommitted changes and existing behavior outside the requested scope.
- Do not rewrite, rename, reformat, or redesign unrelated code.
- Keep fixes focused on the problem. Add limits, caches, deduplication, or cancellation mechanisms only when justified by the task rather than hypothetical future requirements.
- Consider edge cases relevant to the change, including invalid input, missing software, repeated actions, multiple connections, disconnects, and shutdown.
