---
type: Mod Feature
title: The KSA game console as a terminal shell
description: A custom shell that runs typed lines through KSA's TerminalInterface and captures the console's colored output with a Harmony postfix on ConsoleWindow.Print, gated so only output produced during the command is captured
mod: purrtty
category: external-io
techniques: [harmony-postfix, inter-mod-api]
hooks:
  - harmony-postfix Brutal.ImGuiApi.Abstractions.ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8, int)
  - starmap StarMapAfterGui
lifecycle: [frame:draw-ui, thread:background]
game_types:
  - KSA.Program
  - Brutal.ImGuiApi.Abstractions.ConsoleWindow
  - Brutal.ImGuiApi.Abstractions.TerminalInterface
  - Brutal.ImGuiApi.ImColor8
tags: [console, commands, harmony, custom-shell, terminal]
---

"Game Console" is a shell offered in every New Tab / New Window menu on every OS. It gives KSA's
developer console (the one opened in game with its own window) a real terminal front end: line
editing, history, Ctrl+L, Ctrl+C, colors, scrollback and copy/paste. Typed lines go to the same
command interpreter the game console uses; whatever the game prints while the command runs is
copied into the terminal, red for errors.

## How it works

- **Shell.** `GameConsoleShell : BaseLineBufferedShell` (from the custom-shell contract) gets line
  editing and a channel-based output pump for free. `OnStartingAsync` refuses to start until
  `Program.ConsoleWindow` and `Program.TerminalInterface` exist; `SendInitialOutput` writes a banner
  and the prompt. The prompt comes from the `PURRTTY_GAME_PROMPT` launch environment variable,
  stamped from purrTTY's config by `ThemeConfiguration.CreateGameShellLaunchOptions`, so the shell
  assembly never references display-layer types.[^game-shell][^well-known-env]
- **Execute.** On Enter, `ExecuteCommandLine` handles the built-in `clear` (`ESC[3J`), otherwise
  sets a static `_activeInstance` plus `_isExecutingCommand` under a lock and calls
  `Program.TerminalInterface.Execute(commandLine)`, then sends the prompt and clears the flag in a
  `finally`.
- **Capture.** `ConsoleWindowPrintPatch` is a Harmony postfix on
  `ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8, int)`, the overload that string and char prints
  funnel into. A lock-free `GameConsoleShell.IsCapturing` check makes it free when no command is
  running; only then is a `string` allocated and passed to `OnConsolePrint`, which re-checks under
  the lock and sends the text as stdout, or as red stderr when the color equals
  `ConsoleWindow.ErrorColor` or `CriticalColor`.[^print-patch]
- **Plumbing.** Output is queued to the shell's unbounded channel and pumped on a background task
  to `OutputReceived`; `CustomShellPtyBridge` adapts that to `IProcessManager.DataReceived`, so the
  terminal treats it like PTY output (see [custom shell registry](./custom-shell-registry-and-lifecycle.md)).[^channel-shell][^bridge]

## Lifecycle and timing

- Typing reaches the shell through `session.SendInput` -> `CustomShellPtyBridge.Write` ->
  `WriteInputAsync`, which for line-buffered shells completes synchronously. So
  `TerminalInterface.Execute` runs on the KSA main thread during `frame:draw-ui`, inside the
  terminal window's ImGui scope (`TerminalWindow.Render` -> `HandleInput`). Commands that open ImGui
  windows or popups would do so nested in purrTTY's window (not observed; inferred from the call path).
- That is not when KSA runs its own console commands. The stock console submits on the Enter key:
  `Program.OnKey` -> `ConsoleWindowEx.OnKey` -> `ConsoleWindow.HandleInputAction(Submit)` ->
  `TerminalInterface.Execute`, during `Glfw.PollEvents` in `PrepareFrame` (`frame:input`), while the
  physics workers are idle.[^ksa-consolewindowex][^ksa-program] Through purrTTY the same command runs
  after the next physics step has been dispatched, so a command that changes vehicle or simulation
  state (teleports, part or resource edits) runs in the racy window where the workers can overwrite
  or conflict with it. Output-only commands are unaffected.
- The Harmony postfix fires on whatever thread calls `Print`; KSA's `AddPendingMessage` takes its
  own lock, so prints are thread-safe. Any print from another thread during `Execute` is attributed
  to the command (comment on `OnConsolePrint`).
- Captured output is written to the terminal from the shell's output pump thread
  (`thread:background`) through the surface inbox, and appears on the next tick.
- The patch is applied at `startup:all-mods-loaded` and classified as optional, so the rest of
  purrTTY works if the signature drifts.

## Key code

| Path | Role |
|---|---|
| [purrTTY.CustomShells/GameConsoleShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShells/GameConsoleShell.cs#L117-L162) | Execute with the capture window |
| [purrTTY.CustomShells/GameConsoleShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShells/GameConsoleShell.cs#L210-L268) | `OnConsolePrint` and cross-version color normalization |
| [purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs#L25-L53) | The postfix and its overload pin |
| [purrTTY.CustomShellContract/Base/BaseLineBufferedShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/Base/BaseLineBufferedShell.cs) | Line editing, history, control keys |
| [purrTTY.CustomShellContract/Base/BaseChannelOutputShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/Base/BaseChannelOutputShell.cs) | Channel output pump |
| [purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs#L531-L541) | Stamps the prompt env var |
| [purrTTY.CustomShells.Tests/Unit/GameConsoleShellTests.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShells.Tests/Unit/GameConsoleShellTests.cs) | Tests incl. `OnConsolePrint` against real `ImColor8` fields |

## Snippets

```csharp
// purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs L25-L52
[HarmonyPatch(typeof(ConsoleWindow))]
[HarmonyPatch(nameof(ConsoleWindow.Print))]
[HarmonyPatch(new[] { typeof(ReadOnlySpan<char>), typeof(ImColor8), typeof(int) })]
public static class ConsoleWindowPrintPatch
{
    /// <summary>
    ///     Postfix patch that captures console output. Parameter names must match
    ///     the original (<c>text</c>, <c>color</c>) so Harmony injects them.
    /// </summary>
    [HarmonyPostfix]
    public static void Postfix(ReadOnlySpan<char> text, ImColor8 color)
    {
        // Cheap lock-free gate so we only allocate a string while our shell is
        // actively capturing a command's output.
        if (!GameConsoleShell.IsCapturing)
        {
            return;
        }

        try
        {
            GameConsoleShell.OnConsolePrint(new string(text), color);
        }
        catch (Exception)
        {
            // Silently handle errors to avoid disrupting the game console
        }
    }
```

```csharp
// purrTTY.CustomShells/GameConsoleShell.cs L127-L154
        try
        {
            // Set this instance as active so Harmony patch can capture output
            lock (_activeLock)
            {
                _activeInstance = this;
                _isExecutingCommand = true;
            }

            try
            {
                // Execute the command via KSA's TerminalInterface. Output —
                // including Execute's own error text — is captured by the
                // Harmony patch on ConsoleWindow.Print(), so the bool result
                // carries no extra information.
                Program.TerminalInterface.Execute(commandLine);

                SendPrompt();
            }
            finally
            {
                // Clear active instance
                lock (_activeLock)
                {
                    _isExecutingCommand = false;
                    _activeInstance = null;
                }
            }
```

The overloads in build 2026.10.10.5554: string and char prints reach the patched overload, byte
and `ImString` prints do not.

```csharp
// current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs L570-L603
	public void Print(string text, int timeToShow = 5)
	{
		Print(text.AsSpan(), DefaultColor, timeToShow);
	}

	public void Print(ImString text, int timeToShow = 5)
	{
		Print((ReadOnlySpan<byte>)text, DefaultColor, timeToShow);
	}

	public void Print(ReadOnlySpan<char> text, int timeToShow = 5)
	{
		Print(text, DefaultColor, timeToShow);
	}

	public void Print(ReadOnlySpan<byte> text, int timeToShow = 5)
	{
		Print(text, DefaultColor, timeToShow);
	}

	public void Print(ImString text, ImColor8 color, int timeToShow = 5)
	{
		AddPendingMessage((ReadOnlySpan<byte>)text, color, timeToShow);
	}

	public void Print(ReadOnlySpan<byte> text, ImColor8 color, int timeToShow = 5)
	{
		AddPendingMessage(text, color, timeToShow);
	}

	public void Print(ReadOnlySpan<char> text, ImColor8 color, int timeToShow = 5)
	{
		AddPendingMessage(text, color, timeToShow);
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8, int)` | [current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs#L600-L603) | Output sink; postfixed[^ksa-consolewindow] |
| `ConsoleWindow.ErrorColor`, `CriticalColor` (static `ImColor8`) | [current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs#L278-L286) | Error classification |
| `TerminalInterface.Execute(string)` | [current/decomp/Brutal.ImGuiApi.Abstractions/TerminalInterface.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Abstractions/TerminalInterface.cs#L41-L56) | Splits and runs commands; errors are printed, not thrown[^ksa-terminal-interface] |
| `Program.ConsoleWindow`, `Program.TerminalInterface` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L500) | `TerminalInterface => ConsoleWindow.Terminal`[^ksa-program] |
| `ConsoleWindowEx.OnKey` (Enter -> `HandleInputAction(Submit)`) | [current/decomp/KSA/ConsoleWindowEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWindowEx.cs#L14-L38) | Where the stock console executes commands: inside `Program.OnKey`, during `Glfw.PollEvents` |

## Pitfalls and version notes

- The patch pins one overload. An older Brutal used `Print(string, uint, int, ConsoleLineType)`;
  `GameConsoleShell.ToUint` has overloads for both `ImColor8` and `uint` color fields so the same
  source compiles against either.
- Byte-span and `ImString` `Print` overloads go straight to `AddPendingMessage` and bypass the patch;
  a KSA command printing UTF-8 literals would not be captured (comment in the patch, confirmed in the
  5554 decomp).
- `Program.TerminalInterface` dereferences `ConsoleWindow`; null-check `Program.ConsoleWindow` first.
- A shell that executes game commands from a UI hook changes their timing relative to the stock
  console (see Lifecycle and timing); queue state-changing commands to the next `frame:input` or
  wait for the solvers if exact parity matters.
- Each captured `Print` becomes its own line, so multi-colored segments of one logical line wrap;
  output a command produces after `Execute` returns is not captured.
- Parameter names in a Harmony postfix must match the original (`text`, `color`).

## See also

- [Custom shell registry and lifecycle](./custom-shell-registry-and-lifecycle.md)
- [Inter-mod shell contract shared through StarMap assembly imports](./custom-shell-contract-via-assembly-imports.md)
- [Failure-isolated Harmony patching](./failure-isolated-harmony-patching.md)
- [PTY threads to frame tick handoff](./pty-threads-to-frame-tick-handoff.md)
- [purrTTY overview](../overview.md)
- [BaseLineBufferedShell that executes lines through the game console](../../../howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-3.md) - how-to article demonstrating this feature

[^game-shell]: purrTTY.CustomShells/GameConsoleShell.cs
[^well-known-env]: purrTTY.CustomShellContract/WellKnownShellEnvironment.cs
[^print-patch]: purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs
[^channel-shell]: purrTTY.CustomShellContract/Base/BaseChannelOutputShell.cs
[^bridge]: purrTTY.Terminal/Pty/CustomShellPtyBridge.cs
[^ksa-consolewindow]: Brutal.ImGuiApi.Abstractions.ConsoleWindow (decompiled, build 2026.10.10.5554)
[^ksa-terminal-interface]: Brutal.ImGuiApi.Abstractions.TerminalInterface (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-consolewindowex]: KSA.ConsoleWindowEx (decompiled, build 2026.10.10.5554)
