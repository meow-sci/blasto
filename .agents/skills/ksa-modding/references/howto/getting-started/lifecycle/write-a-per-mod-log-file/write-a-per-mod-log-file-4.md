---
type: How-To
title: Log through the game's logging system with one LogCategory in a tiny shared assembly
description: Make a static LogCategory in its own small assembly so every project and importing mod writes to the game's log sinks, including the in-game console
category: getting-started
group: lifecycle
topic: write-a-per-mod-log-file
approach: 4
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/brutal-logging-category]
techniques: [logging, inter-mod-api, starmap-lifecycle]
hooks: [call Brutal.Logging.LogCategory.Make]
lifecycle: [startup:all-mods-loaded]
game_types: [Brutal.Logging.LogCategory, Brutal.Logging.LogSystem, KSA.Program]
tags: [logging, brutal, logcategory, console, shared-assembly, purrtty]
---

The simplest way to log from a mod is not to write a logger. KSA initializes `Brutal.Logging.LogSystem` early with
console, in-game console window and file sinks; a mod creates a `LogCategory` and calls it. purrTTY does this with
one static field in a tiny assembly, `purrTTY.Logging`, shared by every project in the repo and by other mods that
import its contract. Compare with the file loggers of [Rotation to .prev.log, timer flush, runtime debug toggle](./write-a-per-mod-log-file-1.md) to [Path probing across Documents and XDG locations](./write-a-per-mod-log-file-3.md).

## Background: how the game does it

`LogCategory.Make(name)` returns the existing category of that name or creates one whose `ILogger` comes from
`LogSystem.Factory?.CreateLogger(name)` **at that moment**:[current/decomp/Brutal.Logging/LogCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogCategory.cs#L44-L56)

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

The `Program` constructor sets `LogSystem.IsEnabled = true` and initializes it with sinks for the process console
(a custom `KSALogFormatter`), the in-game `ConsoleWindow`, and the Brutal monitor (log file), minimum level
Debug.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L689-L725) The ready-made category for mods that do not want their own is
`DefaultCategory.Log` (`LogCategory.Make("Default")`).[current/decomp/Brutal.Logging/DefaultCategory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/DefaultCategory.cs)

## Lifecycle and timing

The static field initializes on first use, and *when* it is created does not matter: `LogSystem.Initialize` walks
`LogCategory.Categories` and gives every category made before it a logger.[current/decomp/Brutal.Logging/LogSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Logging/LogSystem.cs#L13-L24)
What matters is when you *log*: `LogSystem.IsEnabled` stays false until the `Program` constructor sets it, so
lines logged at `StarMapBeforeMain` are dropped silently (use `Console.WriteLine` there). From
`StarMapImmediateLoad` onward the category writes to all three sinks (purrTTY's first use is `StarMapAllModsLoaded`).
`startup:all-mods-loaded`, main thread; later calls can come from any thread.

## Steps

1. **Create a tiny assembly** (or just a class) with the category:[purrTTY.Logging/Logging.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Logging/Logging.cs)

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

2. **Reference the game's logging assembly without copying it.** `Brutal.Core.Logging` is `Private=false`; the
   project also takes `Microsoft.Extensions.Logging.Abstractions` from NuGet:[purrTTY.Logging/purrTTY.Logging.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Logging/purrTTY.Logging.csproj)

   ```xml
   <!-- purrTTY.Logging/purrTTY.Logging.csproj L17-L23 -->
       <ItemGroup>
           <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
           <Reference Include="Brutal.Core.Logging" Condition="Exists('$(KSAFolder)/Brutal.Core.Logging.dll')">
               <HintPath>$(KSAFolder)/Brutal.Core.Logging.dll</HintPath>
               <Private>false</Private>
           </Reference>
       </ItemGroup>
   ```

3. **Log with the interpolated handlers:** `ModLog.Log.Info($"...")`, `.Debug`, `.Warning`, `.Error(ex)`. They skip
   formatting when the level is disabled and add caller member, file and line (`LogCategory.Info` takes a
   `LogString<Information>` plus `[CallerMemberName]` arguments).
4. **Share it with other mods.** purrTTY lists `purrTTY.Logging` in `ExportedAssemblies` beside its contract,
   because the contract references it:[purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml) that list is not read by the pinned StarMap; what shares the
   category is an importing mod listing `purrTTY.Logging` in its own `ImportedAssemblies`, as gatOS does
   ([Importer lists the contract assembly in ImportedAssemblies](../../dependencies/share-types-between-mods/share-types-between-mods-1.md), [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md)).
5. **Prefix your lines** or choose a unique category name; category names are global, so two mods calling
   `Make("purrTTY")` share one object.

## Complete example

[purrTTY.Logging/Logging.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Logging/Logging.cs) and [purrTTY.Logging/purrTTY.Logging.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Logging/purrTTY.Logging.csproj). A mod that wants the same without a shared assembly can call
`DefaultCategory.Log.Info(...)` directly (KSAero, KER and AdvancedFlightComputer do, with an `[AFC]` style prefix).

## Variations and gotchas

- **Do not touch it on test paths.** Brutal's interpolated `LogString` handler pulls `Microsoft.Extensions.ObjectPool`
  from the game runtime, which a plain test host lacks. purrTTY keeps `ModLog` out of its persistence layer and
  wraps registry logging in a non-inlined `try`/`catch`; the game-free interface of [Game-free logging interface swapped for a Brutal sink at load](./write-a-per-mod-log-file-5.md) is
  the cleaner fix.
- **Chatty Debug logging is visible to players** in the in-game console, because the console window is a sink.
- **Lines logged before `LogSystem` initializes are lost.** The category itself is back-filled with a logger by
  `LogSystem.Initialize`, but calls made while `LogSystem.IsEnabled` is still false return without output, so
  nothing logged from `StarMapBeforeMain` reaches the game log.
- **Library code that takes a `Microsoft.Extensions.Logging.ILogger`** (purrTTY's backend) is not bridged to the
  category; in game its `SessionManager` falls back to `NullLogger` and those messages are discarded unless you
  adapt an `ILogger` over `LogCategory`.
- **No file of your own.** You share the game's log file; filter by category prefix.

## See also

- [Game-free logging interface swapped for a Brutal sink at load](./write-a-per-mod-log-file-5.md), [Log from a mod: per-mod log file, game log category and log-once](./overview.md)
- [Logging through a Brutal LogCategory in a shared assembly](../../../../mods/purrtty/features/brutal-logging-category.md)
- [Share types and design an inter-mod API between StarMap mods](../../dependencies/share-types-between-mods/overview.md), [Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)
