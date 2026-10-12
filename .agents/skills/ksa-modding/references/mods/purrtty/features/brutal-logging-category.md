---
type: Mod Feature
title: Logging through a Brutal LogCategory in a shared assembly
description: Give the mod one Brutal.Logging LogCategory in a tiny assembly so every project, and other mods that import it, log into KSA's console, stdout and monitor with interpolated-string handlers and caller info
mod: purrtty
category: getting-started
techniques: [inter-mod-api]
hooks: [static-init purrTTY.Logging.ModLog.Log]
lifecycle: [startup:all-mods-loaded]
game_types:
  - Brutal.Logging.LogCategory
  - Brutal.Logging.LogSystem
  - KSA.Program
  - Brutal.ImGuiApi.Abstractions.ConsoleWindow
tags: [logging, brutal, diagnostics, inter-mod]
---

All purrTTY projects log through one static field, `ModLog.Log`, a Brutal `LogCategory` named
"purrTTY". It lives in its own assembly (`purrTTY.Logging`) so the backend, the display layer, the
custom-shell contract and the game mod share it, and so other mods that import the contract get
the same category. Messages show up wherever KSA routes its own logs.

## How it works

- `ModLog.Log = LogCategory.Make("purrTTY")`. `Make` returns an existing category of that name or
  creates one whose `ILogger` comes from `LogSystem.Factory?.CreateLogger(name)` at that moment.[^logging][^ksa-logcategory]
- Calls use Brutal's interpolated string handlers (`ModLog.Log.Debug($"...")`), which skip
  formatting when the level is disabled and add caller member/file/line.
- KSA initializes `LogSystem` early in `Program` startup with providers for the console (custom
  `KSALogFormatter`), the in-game `ConsoleWindow`, and the Brutal monitor, minimum level Debug, so
  purrTTY's debug lines appear in the in-game console as well as stdout.[^ksa-program]
- The csproj references `Brutal.Core.Logging` with `Private=false` (the game supplies it) and
  `Microsoft.Extensions.Logging.Abstractions`.[^logging-csproj]
- `purrTTY.Logging` is listed in purrTTY's `ExportedAssemblies` beside the contract because the
  contract references it.[^mod-toml] That list is not read by the pinned StarMap; what makes another
  mod share the category is listing `purrTTY.Logging` in its own `ImportedAssemblies`, as gatOS does
  (see [the contract feature](./custom-shell-contract-via-assembly-imports.md)).

## Lifecycle and timing

The static field initializes on first use. In game that is during `StarMapAllModsLoaded`
(`OnFullyLoaded` logs first), after `LogSystem.Initialize` has run in the `Program` constructor, so
the category gets a real logger. purrTTY's `[StarMapImmediateLoad]` method also logs, but it takes no
`KSA.Mod` parameter, so the pinned StarMap never calls it.
If a category were created before `LogSystem` was initialized its `Logger` would stay null, and
`Log` throws `LogNotInitializedException` when logging is enabled.[^ksa-logcategory]

## Key code

| Path | Role |
|---|---|
| [purrTTY.Logging/Logging.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Logging/Logging.cs) | The category |
| [purrTTY.Logging/purrTTY.Logging.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Logging/purrTTY.Logging.csproj) | Game-supplied reference |
| [purrTTY.CustomShellContract/CustomShellRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/CustomShellRegistry.cs#L300-L318) | Defensive use in code that also runs in tests |

## Snippets

```csharp
// purrTTY.Logging/Logging.cs L1-L12
using Brutal.Logging;

namespace purrTTY.Logging;

/// <summary>
///     purrTTY logging category
/// </summary>
public class ModLog
{
  public static readonly LogCategory Log = LogCategory.Make("purrTTY");

}
```

```csharp
// current/decomp/Brutal.Logging/LogCategory.cs L44-L56
	public static LogCategory Make(string categoryName)
	{
		if (Categories.TryGetValue(categoryName, out LogCategory value))
		{
			return value;
		}
		value = new LogCategory(categoryName)
		{
			Logger = LogSystem.Factory?.CreateLogger(categoryName)
		};
		Categories[categoryName] = value;
		return value;
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `LogCategory.Make(string)`, `Debug/Info/Warning/Error(...)` | [current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs) | The logging API |
| `LogSystem.Initialize`, `LogSystem.IsEnabled` | [current/decomp/Brutal.Logging/LogSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogSystem.cs) | Global switch and factory[^ksa-logsystem] |
| `Program` logging setup | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L709-L723) | Providers: console, `ConsoleWindow`, monitor |

## Pitfalls and version notes

- Code that also runs in a plain test host must not touch `ModLog` on paths the tests execute:
  Brutal's interpolated `LogString` handler pulls `Microsoft.Extensions.ObjectPool` from the game
  runtime, which the reference-assembly set used by tests lacks (gotcha 30). purrTTY keeps
  `ModLog` out of its layout persistence layer and wraps registry logging in a non-inlined method
  inside try/catch.[^gotchas][^registry]
- Because KSA's `ConsoleWindow` is a log provider, chatty Debug logging is visible to players in the
  in-game console; the Game Console shell's capture postfix would also see log lines printed while
  a command runs.
- Category names are global: two mods calling `Make("purrTTY")` share one object.
- The renderer-neutral backend logs through a `Microsoft.Extensions.Logging.ILogger` passed to its
  constructors, not through `ModLog`. In game, `GhosttySessionManagerFactory.CreateSessionManager`
  is called without a logger, so `SessionManager` falls back to `NullLogger` and backend messages
  (inbox drops, PTY write errors, the packed-cell layout check) are discarded. Bridge the two with
  an `ILogger` adapter over `LogCategory` if you need them.[^manager-factory][^session-manager]

## See also

- [Inter-mod shell contract shared through StarMap imports](./custom-shell-contract-via-assembly-imports.md)
- [Headless test suites](./headless-test-suites.md)
- [Game console shell](./game-console-shell.md)
- [purrTTY overview](../overview.md)
- [Log through the game's logging system with one LogCategory in a tiny shared assembly](../../../howto/getting-started/lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-4.md) - how-to article demonstrating this feature

[^logging]: purrTTY.Logging/Logging.cs
[^ksa-logcategory]: Brutal.Logging.LogCategory (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^logging-csproj]: purrTTY.Logging/purrTTY.Logging.csproj
[^mod-toml]: purrTTY.GameMod/mod.toml
[^ksa-logsystem]: Brutal.Logging.LogSystem (decompiled, build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
[^registry]: purrTTY.CustomShellContract/CustomShellRegistry.cs
[^manager-factory]: purrTTY.Display/Ghostty/GhosttySessionManagerFactory.cs
[^session-manager]: purrTTY.Terminal/Sessions/SessionManager.cs
