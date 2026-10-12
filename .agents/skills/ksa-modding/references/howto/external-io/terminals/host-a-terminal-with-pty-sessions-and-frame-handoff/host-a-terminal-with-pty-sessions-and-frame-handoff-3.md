---
type: How-To
title: BaseLineBufferedShell that executes lines through the game console
description: Add a custom terminal shell that runs lines through the KSA developer console and captures the coloured output with a gated Harmony postfix on ConsoleWindow.Print
category: external-io
group: terminals
topic: host-a-terminal-with-pty-sessions-and-frame-handoff
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/purrtty/features/custom-shell-registry-and-lifecycle, mods/purrtty/features/game-console-shell]
techniques: [inter-mod-api, harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix Brutal.ImGuiApi.Abstractions.ConsoleWindow.Print]
lifecycle: [frame:draw-ui, startup:all-mods-loaded, thread:background]
game_types:
  - KSA.Program
  - Brutal.ImGuiApi.Abstractions.TerminalInterface
  - Brutal.ImGuiApi.Abstractions.ConsoleWindow
  - Brutal.ImGuiApi.ImColor8
tags: [terminal, custom-shell, game-console, harmony, output-capture, line-editing]
---

Outcome: a terminal tab that is not an OS process: lines go in, coloured text comes out, and the work happens in your C# code. The worked shell executes lines through KSA's own developer console and captures what the game prints while the command runs. Prerequisites: StarMap and the purrTTY custom-shell contract assembly. Demonstrated by [Custom shell registry and lifecycle](../../../../mods/purrtty/features/custom-shell-registry-and-lifecycle.md) and [Game console shell](../../../../mods/purrtty/features/game-console-shell.md) in purrTTY.

## Background: how the game does it

KSA has a developer console. `Program.ConsoleWindow` is the window and `Program.TerminalInterface` is a property that returns its `Terminal`, so a mod cannot reach the interface before the window exists:

```csharp
// current/decomp/KSA/Program.cs L500
	public static TerminalInterface TerminalInterface => ConsoleWindow.Terminal;
```

`TerminalInterface.Execute(string)` splits the input into commands, runs each, prints errors instead of throwing, and returns `false` after a `TerminalInterfaceException`:

```csharp
// current/decomp/Brutal.ImGuiApi.Abstractions/TerminalInterface.cs L41-L56
	public bool Execute(string inInput)
	{
		foreach (string item in Parser.SplitToCommands(inInput))
		{
			try
			{
				ProcessCommand(item);
			}
			catch (TerminalInterfaceException ex)
			{
				OutputProcessCommandError(item, ex.Message);
				return false;
			}
		}
		return true;
	}
```

Output goes through `ConsoleWindow.Print`. String and char prints funnel into one overload, while byte-span and `ImString` prints call `AddPendingMessage` directly:[^ksa-consolewindow-cs]

```csharp
// current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs L570-L573, L595-L598, L600-L603
	public void Print(string text, int timeToShow = 5)
	{
		Print(text.AsSpan(), DefaultColor, timeToShow);
	}
// ...
	public void Print(ReadOnlySpan<byte> text, ImColor8 color, int timeToShow = 5)
	{
		AddPendingMessage(text, color, timeToShow);
	}
// ...
	public void Print(ReadOnlySpan<char> text, ImColor8 color, int timeToShow = 5)
	{
		AddPendingMessage(text, color, timeToShow);
	}
```

## Lifecycle and timing

| Work | Where |
|---|---|
| Register the shell | `[StarMapAllModsLoaded]` of the registering mod (`startup:all-mods-loaded`), or any later time; the registry is a `ConcurrentDictionary` |
| `StartAsync` | the session-creation pool thread |
| `WriteInputAsync` | the main thread, inside the tick (`frame:draw-ui`); a line-buffered shell completes it synchronously |
| `ExecuteCommandLine`, so `TerminalInterface.Execute` | the main thread, inside the terminal window's ImGui scope |
| Captured output | queued to the shell's channel and delivered by the output pump task (`thread:background`), landing in the surface inbox |

**This is not when the stock console runs commands.** KSA's own console submits on the Enter key: `Program.OnKey` calls `ConsoleWindow.OnKey` (an extension method, `ConsoleWindowEx.OnKey`), which maps the submit key to `HandleInputAction(Submit)`, and `ConsoleWindow.Submit` calls `Terminal.Execute`:

```csharp
// current/decomp/KSA/Program.cs L1745-L1750
	public void OnKey(GlfwWindow window, GlfwKey key, int scanCode, GlfwKeyAction action, GlfwModifier mods)
	{
		GlfwKeyEvent keyEvent = new GlfwKeyEvent(window, action, key, mods);
		ShiftDown = EnumEx.IsSet((int)mods, 1);
		AltDown = EnumEx.IsSet((int)mods, 4);
		if (IsLoaded && !GameSettings.OnKeyAll(keyEvent) && !Popup.OnKeyAll(keyEvent) && !ConsoleWindow.OnKey(window, key, scanCode, action, mods) && !ConsoleWindow.IsOpen && !ImGui.GetIO().WantCaptureKeyboard)
```

```csharp
// current/decomp/KSA/ConsoleWindowEx.cs L28-L31
		if (key == SubmitKey)
		{
			return console.HandleInputAction(ConsoleWindow.Actions.Submit);
		}
```

GLFW delivers key callbacks from inside `Glfw.PollEvents` (standard GLFW behaviour; the game subscribes `Program.OnKey` to its window), which `PrepareFrame` calls between `ApplyVehicleSolvers` and `ExecuteNextVehicleSolvers`, while the workers are idle:

```csharp
// current/decomp/KSA/Program.cs L2141, L2167, L2179, L2220
	private OnFrameResult PrepareFrame(double currentPlayerTime, double dtPlayer)
// ...
		Universe.ApplyVehicleSolvers();
// ...
		Glfw.PollEvents();
// ...
		Universe.ExecuteNextVehicleSolvers(dtPlayer, jobSimStep);
```

Through a purrTTY tab the same command runs at `frame:draw-ui`, after the next physics step was dispatched. Output-only commands are unaffected; a command that changes vehicle or simulation state runs in the window where workers can overwrite it ([lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#why-main-thread-mutation-after-dispatch-is-racy)).

## Steps

1. **Implement `ICustomShell`, or derive from a base class.** The interface is small: metadata, running flag, two events, start and stop, input, resize, cancellation and an initial-output hook the host calls once after wiring:

   ```csharp
   // purrTTY.CustomShellContract/ICustomShell.cs L12, L17, L22, L27, L37, L44, L53, L60, L65, L74
       CustomShellMetadata Metadata { get; }
   // ...
       bool IsRunning { get; }
   // ...
       event EventHandler<ShellOutputEventArgs>? OutputReceived;
   // ...
       event EventHandler<ShellTerminatedEventArgs>? Terminated;
   // ...
       Task StartAsync(CustomShellStartOptions options, CancellationToken cancellationToken = default);
   // ...
       Task StopAsync(CancellationToken cancellationToken = default);
   // ...
       Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
   // ...
       void NotifyTerminalResize(int width, int height);
   // ...
       void RequestCancellation();
   // ...
       void SendInitialOutput();
   ```

   `BaseLineBufferedShell` adds line editing, history, Ctrl+C, Ctrl+L, Ctrl+W and arrow keys on top of a channel-based output pump; a derived shell supplies three members:

   ```csharp
   // purrTTY.CustomShellContract/Base/BaseLineBufferedShell.cs L113, L119, L125
       protected abstract void ExecuteCommandLine(string commandLine);
   // ...
       protected abstract void HandleClearScreen();
   // ...
       protected abstract string GetPrompt();
   ```

2. **Keep the constructor trivial; do real work in `StartAsync`.** The registry creates one probe instance to validate the metadata and disposes it, so a constructor must not touch the game, the network or a VM, and `Dispose` must work on a never-started instance. Name, description and author must be non-blank and the version non-null, or registration throws:

   ```csharp
   // purrTTY.CustomShellContract/CustomShellRegistry.cs L42-L77
       public void RegisterShell<T>(string shellId, Func<T> factory) where T : class, ICustomShell
       {
           if (string.IsNullOrWhiteSpace(shellId))
           {
               throw new ArgumentException("Shell ID cannot be null or empty", nameof(shellId));
           }

           if (factory == null)
           {
               throw new ArgumentNullException(nameof(factory));
           }

           try
           {
               // Validate the shell implementation by creating a temporary instance
               using var testInstance = factory();
               ValidateShellImplementation(testInstance, shellId);

               // Store the factory atomically: TryAdd makes the duplicate check and
               // the insert one operation (a ContainsKey-then-set pair lets two
               // concurrent registrations of the same ID both pass the guard).
               // Func<T> is covariant, so the factory is stored directly — no
               // wrapping closure needed.
               if (!_shellFactories.TryAdd(shellId, factory))
               {
                   throw new ArgumentException($"Shell with ID '{shellId}' is already registered", nameof(shellId));
               }

               // Store metadata from the test instance
               _shellMetadata[shellId] = testInstance.Metadata;
           }
           catch (Exception ex) when (!(ex is ArgumentException || ex is ArgumentNullException))
           {
               throw new CustomShellRegistrationException($"Failed to register shell '{shellId}': {ex.Message}", ex);
           }
       }
   ```

3. **Verify the game console plumbing before starting.** Null-check `Program.ConsoleWindow` first, because `Program.TerminalInterface` dereferences it:

   ```csharp
   // purrTTY.CustomShells/GameConsoleShell.cs L52-L66
       protected override Task OnStartingAsync(CustomShellStartOptions options, CancellationToken cancellationToken)
       {
           // Verify the game console plumbing is available. Program.ConsoleWindow
           // must be null-checked first: Program.TerminalInterface dereferences it
           // (`=> ConsoleWindow.Terminal`), so probing TerminalInterface alone
           // throws the very NRE this guard exists to convert into a clear error.
           if (Program.ConsoleWindow is null || Program.TerminalInterface is null)
           {
               throw new InvalidOperationException(
                   "KSA TerminalInterface is not available. The game may not be fully initialized yet.");
           }

           ApplyPromptFromOptions(options);
           return Task.CompletedTask;
       }
   ```

4. **Execute the line and gate the capture to its extent.** Set the active instance and a flag under a lock, call `Program.TerminalInterface.Execute`, then clear the flag in `finally`. The flag is the only thing that makes the print hook capture:

   ```csharp
   // purrTTY.CustomShells/GameConsoleShell.cs L118-L162
       protected override void ExecuteCommandLine(string commandLine)
       {
           // Check if it's a built-in command first
           if (TryHandleBuiltinCommand(commandLine))
           {
               SendPrompt();
               return;
           }

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
           }
           catch (Exception ex)
           {
               // Handle any exceptions during command execution - send as stderr
               SendError($"\x1b[31mError executing command: {ex.Message}\x1b[0m\r\n");
               SendPrompt();
           }
       }
   ```

5. **Capture the output with a gated Harmony postfix.** The patch pins the one `Print(ReadOnlySpan<char>, ImColor8, int)` overload and does nothing, not even a string allocation, unless a command is executing. Parameter names must match the original (`text`, `color`):

   ```csharp
   // purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs L25-L53
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
   }
   ```

6. **Colour by the game's own error colours.** `OnConsolePrint` re-checks the active instance under the lock, then classifies a print as an error when its colour equals `ConsoleWindow.ErrorColor` or `CriticalColor` and forwards it as red stderr, otherwise as stdout:

   ```csharp
   // purrTTY.CustomShells/GameConsoleShell.cs L241-L249
                   uint c = color.AsUint();
                   bool isError = c == ToUint(ConsoleWindow.ErrorColor) || c == ToUint(ConsoleWindow.CriticalColor);

                   if (isError)
                   {
                       // Send errors as stderr with red formatting
                       string formattedError = $"\x1b[31m{output}\x1b[0m\r\n";
                       _activeInstance.SendError(formattedError);
                   }
   ```

7. **Register the shell and read the prompt without coupling.** The built-in shell is discovered by assembly scan; another mod calls `RegisterShell` explicitly (see [the SSH approach](host-a-terminal-with-pty-sessions-and-frame-handoff-4.md)). Settings travel as launch environment variables, so the shell assembly never references the display layer's config types.

## Complete example

| File | Role |
|---|---|
| [purrTTY.CustomShellContract/ICustomShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/ICustomShell.cs) | The interface |
| [purrTTY.CustomShellContract/CustomShellRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/CustomShellRegistry.cs) | Validated registration and discovery |
| [purrTTY.CustomShellContract/Base/BaseLineBufferedShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/Base/BaseLineBufferedShell.cs) | Line editing and history |
| [purrTTY.CustomShells/GameConsoleShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShells/GameConsoleShell.cs) | The game-console shell |
| [purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patches/ConsoleWindowPrintPatch.cs) | The capture postfix |
| [purrTTY.Terminal/Pty/CustomShellPtyBridge.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/CustomShellPtyBridge.cs) | Adapts a shell to the pty manager interface |

## Variations and gotchas

- **The patch pins one overload.** Byte-span and `ImString` prints bypass it, so a command printing UTF-8 literals directly would not be captured. An older API used a different `Print` signature; re-verify the funnel when the game updates.
- **Capture is bounded to the call.** Output a command produces after `Execute` returns is lost, and unrelated cross-thread prints during `Execute` are attributed to it. Each captured print becomes its own line.
- **Timing differs from the stock console** (see above). Queue state-changing commands to a settled point if exact parity matters ([Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)).
- **Null-check `Program.ConsoleWindow`** before `Program.TerminalInterface`.
- **A fully asynchronous shell** gets no cross-call ordering guarantee from the bridge; a line-buffered shell completes synchronously and keeps order.

## See also

- [Overview](overview.md); previous: [the surface](host-a-terminal-with-pty-sessions-and-frame-handoff-2.md); next: [an SSH shell](host-a-terminal-with-pty-sessions-and-frame-handoff-4.md)
- [Custom shell registry and lifecycle](../../../../mods/purrtty/features/custom-shell-registry-and-lifecycle.md), [Game console shell](../../../../mods/purrtty/features/game-console-shell.md)
- [Share types between mods](../../../getting-started/dependencies/share-types-between-mods/overview.md): how the contract assembly is shared
- [Match the stock console UI style](../../../ui/windows/match-the-stock-console-ui-style/overview.md)

[^ksa-consolewindow-cs]: KSA decomp: Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs (build 2026.10.10.5554)
