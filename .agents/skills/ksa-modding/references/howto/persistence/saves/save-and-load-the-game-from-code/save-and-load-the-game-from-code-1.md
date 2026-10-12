---
type: How-To
title: Write timed native saves with SaveName.TryAccept and UncompressedSave.Make
description: Save the game every N real seconds through the same two calls as the console save command, refusing in the editor and keeping the timer independent of warp and pause
category: persistence
group: saves
topic: save-and-load-the-game-from-code
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/save-yourself-timed-autosave]
techniques: [starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, harmony-prefix KSA.Program.OnDrawUiConsole]
lifecycle: [frame:draw-ui, event:save]
game_types: [KSA.GameSaves, KSA.UncompressedSave, KSA.SaveName, KSA.Program, KSA.Universe]
tags: [saves, autosave, timer, save-name, stopwatch]
---

Outcome: a mod writes a fresh native save every N seconds, named `<prefix>_YYYYMMDDTHHMMSS`, that is indistinguishable from a manual save, so other mods' save patches and sidecars run too. unscience's Save Yourself submod does this without a single Harmony patch; it only calls two game methods. Prerequisite: a StarMap mod that has a per-frame hook. Demonstrated by [Timed auto saves through the game's own save path](../../../../mods/unscience/features/save-yourself-timed-autosave.md).

## Background: how the game does it

The console `save` command is the template (`GameSaves.MakeUncompressedSave`):

```csharp
// current/decomp/KSA/GameSaves.cs L273-L280
	[TerminalAction("save", "save the game", ArgParseMode.Default)]
	public static void MakeUncompressedSave(string name)
	{
		if (!RefusedInEditor() && SaveName.TryAccept(name, out name))
		{
			UncompressedSave.Make(name);
		}
	}
```

`Make` constructs the save, calls `Populate()` (which builds `UniverseData` from the running world) and `Write()`, which replaces the folder `saves/<name>` under `Constants.DocumentsFolderPath` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124)). It returns `null` if the write failed ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146)). Two details matter for a timer:

- `RefusedInEditor` logs an error and raises a visible `TimedAlert` every time it refuses. A timer that keeps asking while the editor is open would flash a warning each attempt, so a mod should test `Program.IsEditorOpen` itself first ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L476)).
- `SaveName.TryAccept` ([current/decomp/KSA/SaveName.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveName.cs#L60-L69)) may change the name: it drops disallowed characters, truncates to `SaveName.MAX_LENGTH` (64) and may append `_`. A name you generate should survive it unchanged, otherwise your own record of "the save I just wrote" no longer matches the save list.

## Lifecycle and timing

The save is a main-thread call, made inside the UI phase of `Program.OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2275)). unscience's host drives submods from `[StarMapBeforeGui]`, the prefix on `OnDrawUiFrame`, which only runs when the HUD is shown; while the HUD is hidden (`DrawUI == false`) it replays the same callback from a prefix on `Program.OnDrawUiConsole`, which is always called ([ksa-abstractions.lib/HiddenUiFrameHook.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/HiddenUiFrameHook.cs#L62-L71), host: [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L150-L167)). Lifecycle terms are defined in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

What that implies:

- The vehicle and orbit solvers for the next step were dispatched earlier in `PrepareFrame`, so they are running while `Populate` reads the published main-thread state. KSA's own save window does exactly this; a mod that needs a stricter point should read [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md).
- The frame stalls for the write. `Make` writes the whole `universe.xml` synchronously. Keep intervals long (the mod clamps to 5-300 s, default 30).
- The editor gate is re-checked every frame, so a save that falls due while the editor is open fires on the first UI phase after it closes.

## Steps

1. **Gate on world loaded and editor closed.** `Universe.CurrentSystem` is null until a system is loaded. unscience wraps both tests and the save call in one helper:

   ```csharp
   // ksa-abstractions.lib/GameSaveProvider.cs L18-L36
   /// <summary>True when KSA would accept a save request right now.</summary>
   public static bool CanSaveNow => IsWorldLoaded && !IsEditorOpen;

   /// <summary>Longest save name KSA accepts before truncating (<c>SaveName.MAX_LENGTH</c>).</summary>
   public static int MaxSaveNameLength => SaveName.MAX_LENGTH;

   /// <summary>
   /// Creates (or overwrites) a save named <paramref name="name"/> the way the terminal <c>save</c>
   /// command does: editor refusal, <c>SaveName.TryAccept</c> sanitising, then <c>UncompressedSave.Make</c>.
   /// Returns false when KSA refused the request or could not write the files; KSA logs and alerts itself.
   /// </summary>
   public static bool TryMakeSave(string name, out string acceptedName)
   {
       acceptedName = string.Empty;
       if (!CanSaveNow) return false;
       if (!SaveName.TryAccept(name, out string sanitized)) return false;
       acceptedName = sanitized;
       return UncompressedSave.Make(sanitized) != null;
   }
   ```

   `TryMakeSave` mirrors the console command but returns a `bool`, so the caller can report failure. `UncompressedSave.Make` returning `null` covers a failed write; the game has already logged and alerted.

2. **Build names the game will not alter.** Restrict the prefix to ASCII letters, digits, `_` and `-`, which is stricter than `SaveName`, and cap it at `64 - 16` so the 16-character timestamp suffix fits:

   ```csharp
   // save-yourself.lib/AutoSaveSettings.cs L45-L67
   public static bool IsAllowedPrefixChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-';

   /// <summary>True for an empty prefix or one made only of allowed characters within the length limit.</summary>
   public static bool IsValidPrefix(string? prefix)
   {
       if (prefix == null || prefix.Length > MaxPrefixLength) return false;
       foreach (char c in prefix)
           if (!IsAllowedPrefixChar(c)) return false;
       return true;
   }

   /// <summary>Drops disallowed characters and truncates to <see cref="MaxPrefixLength"/>.</summary>
   public static string SanitizePrefix(string? raw)
   {
       if (string.IsNullOrEmpty(raw)) return string.Empty;
       var builder = new StringBuilder(Math.Min(raw.Length, MaxPrefixLength));
       foreach (char c in raw)
       {
           if (builder.Length == MaxPrefixLength) break;
           if (IsAllowedPrefixChar(c)) builder.Append(c);
       }
       return builder.ToString();
   }
   ```

   The name builder (`AutoSaveNaming.BuildName`, [save-yourself.lib/AutoSaveNaming.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveNaming.cs#L19-L24)) formats the timestamp with `yyyyMMdd'T'HHmmss` and `CultureInfo.InvariantCulture`, and avoids a double separator:

   ```csharp
   // save-yourself.lib/AutoSaveNaming.cs L14-L17
   /// <summary>
   /// Joins the prefix and the timestamp with an underscore. A prefix that already ends in an
   /// underscore or hyphen is used as-is, so the default <c>autosave_</c> yields
   /// <c>autosave_20260928T203000</c> rather than a double underscore.
   ```

3. **Count real time, not game time.** The controller takes two delegates, so it contains no KSA types and runs in a plain test project. A `Stopwatch` feeds it wall-clock seconds so time warp, pause and frame hitches do not change the cadence:

   ```csharp
   // save-yourself.lib/SaveYourselfSubmod.cs L20-L40
   public AutoSaveController Controller { get; } = new(
       () => GameSaveProvider.CanSaveNow,
       name => WriteSave(name));

   private readonly AutoSaveSettingsStore _store = new();
   private readonly Stopwatch _clock = new();

   public void Initialize()
   {
       Instance = this;
       Controller.Configure(_store.Load());
       _clock.Restart();
   }

   public void Update(double dt)
   {
       // Real seconds since the previous frame, so warp, pause and hitches do not skew the interval.
       double elapsed = _clock.Elapsed.TotalSeconds;
       _clock.Restart();
       Controller.Tick(elapsed);
   }
   ```

4. **Wait for the game instead of retrying every frame.** When the interval has elapsed but `canSave()` is false the controller keeps the countdown full and sets a flag, so the save fires on the first allowed frame; a refused or failed save still restarts the countdown:

   ```csharp
   // save-yourself.lib/AutoSaveController.cs L63-L82
   /// <summary>Advances the countdown by real elapsed seconds and writes a save when it is due.</summary>
   public void Tick(double elapsedRealSeconds)
   {
       if (!Settings.Enabled)
       {
           _elapsedSeconds = 0;
           IsWaitingForGame = false;
           return;
       }
       if (elapsedRealSeconds > 0) _elapsedSeconds += elapsedRealSeconds;
       if (_elapsedSeconds < Settings.IntervalSeconds) return;
       if (!_canSave())
       {
           IsWaitingForGame = true;
           return;
       }
       IsWaitingForGame = false;
       _elapsedSeconds = 0;
       SaveNow();
   }
   ```

5. **Write through the provider and log the outcome:**

   ```csharp
   // save-yourself.lib/SaveYourselfSubmod.cs L62-L69
   private static bool WriteSave(string name)
   {
       bool written = GameSaveProvider.TryMakeSave(name, out string accepted);
       Console.WriteLine(written
           ? $"save-yourself: wrote save '{accepted}'."
           : $"save-yourself: KSA did not write save '{name}'.");
       return written;
   }
   ```

6. **Hook it to a frame callback that survives a hidden HUD.** If your mod uses only `[StarMapBeforeGui]`, the timer silently stops while the player hides the HUD. See [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md) for the prefix on `Program.OnDrawUiConsole`, or use `[StarMapAfterOnFrame]`, which fires on every frame.

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/GameSaveProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/GameSaveProvider.cs) | `CanSaveNow`, `TryMakeSave` |
| [save-yourself.lib/SaveYourselfSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/SaveYourselfSubmod.cs) | Wiring, `Stopwatch`, `WriteSave` |
| [save-yourself.lib/AutoSaveController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveController.cs) | Countdown, wait-for-game, status and error text |
| [save-yourself.lib/AutoSaveSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveSettings.cs) | Prefix sanitising, interval clamp |
| [save-yourself.lib/AutoSaveNaming.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveNaming.cs) | Name builder |

## Variations and gotchas

- **Overwrites.** A name that already exists is replaced (`TryReplace` deletes the folder). Timestamps make each name unique to the second; if you reuse a fixed name, you overwrite the previous autosave.
- **Unsaved sidecars.** Because `Make` runs the real `Populate` and `Write`, a mod with a save patch (for example unscience's scene sidecar) writes its data too. A standalone host that has not installed those patches produces plain saves.
- **Load is not covered.** Nothing here reads a save back; for that see [Load a save from a command file and wait until a craft is flown again](save-and-load-the-game-from-code-2.md).
- **Version risk.** `GameSaves.MakeUncompressedSave` and `UncompressedSave.Make` are the surface this relies on; a changed signature is a compile or `MissingMethodException` failure, not silent wrong behaviour.

## See also

- [Load a save from a command file and wait until a craft is flown again](save-and-load-the-game-from-code-2.md), [Load a save a few seconds after start from an unattended scenario runner](save-and-load-the-game-from-code-3.md), [overview](./overview.md)
- [Timed auto saves through the game's own save path](../../../../mods/unscience/features/save-yourself-timed-autosave.md)
- [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md), [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)
