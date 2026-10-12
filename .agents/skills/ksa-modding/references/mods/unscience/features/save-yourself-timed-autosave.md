---
type: Mod Feature
title: Timed auto saves through the game's own save path
description: Save Yourself writes a new native KSA save every N wall-clock seconds by calling SaveName.TryAccept and UncompressedSave.Make the way the console save command does, so every native save hook and the unscience sidecar run exactly as for a manual save
mod: unscience
submod: save-yourself
category: persistence
techniques: [starmap-lifecycle, toml-config, save-sidecar, dependency-injection, managed-tests]
hooks:
  - starmap StarMapBeforeGui (ISubmod.Update via unscience Mod.UpdateSubmods)
  - harmony-prefix KSA.Program.OnDrawUiConsole (HiddenUiFrameHook replay while the HUD is hidden)
  - starmap StarMapAfterGui (panel and Save Now button)
lifecycle: [frame:draw-ui, event:save]
game_types:
  - KSA.UncompressedSave
  - KSA.GameSave
  - KSA.GameSaves
  - KSA.SaveName
  - KSA.UniverseData
  - KSA.Universe
  - KSA.Program
tags: [saves, autosave, timer, toml, settings, testing, sidecar, dependency-injection]
---

Save Yourself writes a fresh native save every N real seconds (5-300, default 30), named
`<prefix>_YYYYMMDDTHHMMSS` in local time, for example `autosave_20260928T203000`. It does not
serialize anything itself and installs no Harmony patches: it calls the same two game methods the
console `save` command calls, so the game builds `universe.xml`, refreshes the save list and fires every
mod's save hooks exactly as for a manual save. In unscience that includes the scene sidecar, so every
auto save folder also gets its `unscience.json`.[^sy-readme][^game-save-provider]

Mechanism in one line: `ISubmod.Update` feeds `Stopwatch` deltas into an `AutoSaveController` whose game
access is two injected delegates; when the interval has elapsed and
`GameSaveProvider.CanSaveNow` (world loaded, editor closed) is true it calls
`GameSaveProvider.TryMakeSave`, which is `SaveName.TryAccept` followed by `UncompressedSave.Make`.

## How it works

**The game's save entry point.** `GameSaves.MakeUncompressedSave(string)` is the `[TerminalAction("save")]`
console command: refuse in the editor (`RefusedInEditor`, which logs and raises a timed alert), sanitize
the name with `SaveName.TryAccept`, then `UncompressedSave.Make(name)`. `Make` constructs the save,
calls `Populate()` (`UniverseData.Create()`: game time, camera, every celestial system, kitten roster)
and `Write()`, which replaces the folder `<Documents>/My Games/Kitten Space Agency/saves/<name>`,
writes metadata and `universe.xml`, and registers the save with the list (a `LookupCollection` with
`allowReplacing: true`). It returns the save, or `null` when the write failed.[^ksa-gamesaves][^ksa-uncompressedsave][^ksa-universedata]

**`GameSaveProvider`**
([ksa-abstractions.lib/GameSaveProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/GameSaveProvider.cs)),
a shared helper any mod can use, mirrors that path with two changes:

- `CanSaveNow` = `Universe.CurrentSystem != null && !Program.IsEditorOpen`. It mirrors
  `RefusedInEditor` without its alert, so a timer that keeps finding the editor open does not spam
  warnings, and it also refuses when no system is loaded.
- `TryMakeSave(name, out acceptedName)` returns a `bool` (refused, rejected name or failed write all
  return `false`) and reports the sanitized name.

**Naming and settings** ([save-yourself.lib/AutoSaveSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveSettings.cs),
[save-yourself.lib/AutoSaveNaming.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveNaming.cs)):

| Rule | Value | Why |
|---|---|---|
| Prefix alphabet | ASCII letters, digits, `_`, `-` (`char.IsAsciiLetterOrDigit`) | Stricter than KSA's `SaveName` (which also allows Unicode letters and single spaces) so `TryAccept` never changes the name |
| Prefix length | at most 48 = 64 - 16 | `SaveName.MAX_LENGTH` is 64 and the `_YYYYMMDDTHHMMSS` suffix is 16 characters; `Sanitize` silently stops at 64 |
| Empty prefix | `EffectivePrefix` = `autosave_` | Default name |
| Separator | `_` added unless the prefix already ends in `_` or `-` | `autosave_` gives `autosave_2026...`, not a double underscore |
| Timestamp | `yyyyMMdd'T'HHmmss`, `CultureInfo.InvariantCulture`, local time | Sortable, one name per second |
| Interval | `Math.Clamp(seconds, 5, 300)`, default 30 | Applied by `Normalized()` on every configure, load and save |

The panel sanitizes the prefix as you type (disallowed characters are dropped from the input box) and
shows a live **Next name** preview.[^sy-submod-ui][^ksa-savename]

**Controller** ([save-yourself.lib/AutoSaveController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveController.cs)).
Constructed with `Func<bool> canSave`, `Func<string, bool> makeSave` and an optional
`Func<DateTime> now`, so it compiles and runs without KSA. `Tick(elapsedRealSeconds)`:

1. disabled: reset the countdown and the waiting flag;
2. accumulate only positive deltas (negative and `NaN` are ignored);
3. not due: return;
4. due but `canSave()` is false: set `IsWaitingForGame` and keep the countdown full, so the save fires on
   the first frame the game allows it;
5. due and allowed: reset the countdown, then `SaveNow()`.

`SaveNow` builds the name, calls `makeSave` inside `try/catch`, and records `LastSaveName`,
`LastSaveTime`, `SaveCount` or a readable `LastError` ("KSA did not write ...", "Saving ... failed: ...",
"No world is loaded or the vehicle editor is open."). A refused or failed save still restarts the
countdown instead of retrying every frame. The **Save Now** button calls `SaveNow` directly and leaves
the countdown alone.

**Wall clock.** `SaveYourselfSubmod.Update(dt)` ignores the game's `dt` and measures real time with a
`Stopwatch` restarted every call, so time warp, pause and frame hitches do not change the cadence.

**Settings file** ([save-yourself.lib/AutoSaveSettingsStore.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveSettingsStore.cs)).
`.unscience/save-yourself.toml` under `KsaPaths.ModDataDir` (`<Documents>/My Games/Kitten Space Agency/.unscience`,
the same base folder the game's `Constants.DocumentsFolderPath` uses), one `[auto_save]` table with
`prefix`, `enabled`, `interval_seconds`, read and written with Tomlyn. `Load` returns defaults for a
missing file, a parse failure, a missing table or any key of the wrong type, and normalizes the rest
(an over-large integer is clamped before the `int` cast). `Save` normalizes, creates the folder and
never throws. `SaveYourselfSubmod.ApplySettings` writes through on every UI change.[^sy-store][^ksa-paths][^ksa-constants]

The settings are deliberately **not** scene data: Save Yourself registers no save participant, so
loading a save never changes the auto-save configuration and the countdown keeps running across loads.
Countdown position, last name and counters are transient.[^scope-saves]

**How the sidecar rides along.** Because `UncompressedSave.Make` runs the real `Populate` and `Write`,
the unscience host's [native save hooks](./lib-native-save-hooks.md) see an ordinary save: the
`GameSave.Populate` postfix captures every participant's detached state, and the `UncompressedSave.Write`
postfix, only when the native write returned `true`, hands the save to the
[scene sidecar](./unscience-scene-save-sidecar.md), which writes `unscience.json` next to `universe.xml`.
Save Yourself never calls those hooks itself.[^native-save-hooks][^scope-saves] The standalone
`save-yourself` development host does not install the native save hooks, so its saves have no sidecar.

## Lifecycle and timing

| Host | Call chain (build 5554) | Lifecycle |
|---|---|---|
| unscience, HUD visible | `Program.OnFrame` (L2238) -> `if (DrawUI)` -> `OnDrawUiFrame` (L2267); StarMap's `[StarMapBeforeGui]` is a prefix on it -> `Mod.OnBeforeUi` -> `UpdateSubmods` -> `SaveYourselfSubmod.Update` | `frame:draw-ui` |
| unscience, HUD hidden (F2) | `OnDrawUiConsole` (always called) -> `HiddenUiFrameHook` prefix -> `BeforeGui` = `UpdateSubmods` | `frame:draw-ui` (same position, after the gated block) |
| Panel, Save Now | `[StarMapAfterGui]` (postfix of `OnDrawUiViewports`) -> toolbox `RenderContent` | `frame:draw-ui` |
| Standalone host | `[StarMapBeforeGui]` only ([save-yourself/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself/Mod.cs#L38-L43)) | `frame:draw-ui`, nothing while the HUD is hidden |

The save therefore runs synchronously on the main thread at the very start of the UI phase, just
before `OnDrawUiFrame`'s own `Popup.DrawAll` (L3134), which is where a GAME SAVES "New" or "Overwrite"
confirmation would run its save. It is the same moment as a manual save from the save window, not the
moment of the console `save` command (that one runs in `Glfw.PollEvents`, `frame:input`, before the next
physics step is dispatched).[^ksa-program][^unscience-mod][^hidden-ui-frame-hook]

What that implies:

- **Physics workers are running.** `PrepareFrame` has already applied the previous step
  (`ApplyVehicleSolvers`, L2167) and dispatched the next one (`ExecuteNextVehicleSolvers` and
  `ExecuteNextOrbitSolvers`, L2220-L2221) before the UI phase begins, so vehicle and orbit workers are
  integrating step N+1 while `UniverseData.Create` serializes. KSA's own save window makes the same
  trade-off. The unscience save hook notes that native `Populate` reads the published main-thread state
  and that joining workers would not change the snapshot's capture time, which is why participants
  capture only detached, mod-owned data.[^ksa-program][^native-save-hooks]
- **The game time saved is the main thread's, as of this frame.** `UniverseData.Create` records
  `Universe.GetElapsedTime()` and the main viewport camera at the moment of the call.[^ksa-universedata]
- **The frame stalls for the write.** `Make` writes the whole `universe.xml` before returning; there is no
  background save. Long intervals keep the hitch rare.
- **Editor and empty-world gates are checked every frame.** `Program.IsEditorOpen` is
  `EditorFlag || Editor != null`, and the editor zeroes the simulation speed while it is open, so a
  waiting save fires on the first UI phase after the editor closes.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/GameSaveProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/GameSaveProvider.cs) | `CanSaveNow`, `TryMakeSave`, `MaxSaveNameLength` around the native path |
| [save-yourself.lib/SaveYourselfSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/SaveYourselfSubmod.cs) | Wiring: injected delegates, `Stopwatch`, settings load and write-through, `Instance` |
| [save-yourself.lib/AutoSaveController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveController.cs) | Countdown, wait-for-game, save execution, status |
| [save-yourself.lib/AutoSaveSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveSettings.cs) | DTO, prefix sanitising, interval clamping |
| [save-yourself.lib/AutoSaveNaming.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveNaming.cs) | `<prefix>_YYYYMMDDTHHMMSS` builder |
| [save-yourself.lib/AutoSaveSettingsStore.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/AutoSaveSettingsStore.cs) | Tomlyn-backed global preference file |
| [save-yourself.lib/SaveYourselfSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/SaveYourselfSubmod.Ui.cs) | Panel: prefix box, interval drag, preview, Save Now, status |
| [save-yourself.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.tests/Program.cs) | Naming, settings and controller checks with fake game access |
| [save-yourself.tests/StoreChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.tests/StoreChecks.cs) | Settings-file checks against a temp folder |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L150-L167) | `UpdateSubmods`, the per-frame driver in the supermod |

## Snippets

The submod wires the controller to the game and to real time:

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

Countdown with wait-for-game:

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

The reusable save helper:

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

The console command it mirrors (game, build 5554):

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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `UncompressedSave.Make(string) : GameSave?` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146) | `Populate` then `Write`; `null` on a failed write |
| `UncompressedSave.Write()` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L139) | Folder replace, metadata, `universe.xml`, `GameSaves.Register` |
| `GameSave.Populate()` / `UniverseData.Create()` | [current/decomp/KSA/UniverseData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseData.cs#L40-L53) | What a save captures |
| `SaveName.TryAccept(string?, out string)`, `SaveName.MAX_LENGTH`, `Sanitize` | [current/decomp/KSA/SaveName.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveName.cs#L8-L70) | Name sanitising, 64-character cap |
| `GameSaves.MakeUncompressedSave`, `RefusedInEditor`, `MakeSave`, `SaveFolderPath` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L198-L280) | The native paths being mirrored |
| `Program.IsEditorOpen` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L476) | Editor gate |
| `Universe.CurrentSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L114) | World-loaded gate |
| `Program.OnFrame`, `PrepareFrame`, `OnDrawUiFrame`, `OnDrawUiConsole` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2216-L2275) | Where the save runs relative to solver dispatch |
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) | Base folder for saves and for `.unscience` |

## Pitfalls and version notes

- **Name collisions overwrite silently.** Names have one-second resolution and `Make` replaces an
  existing folder of the same name without the GAME SAVES overwrite prompt (`MakeSave` is the method
  that asks). A **Save Now** in the same second as a timed save overwrites it. Nothing is pruned either: every auto save is a new folder.[^ksa-gamesaves][^sy-readme]
- **First tick after startup.** `Initialize` restarts the `Stopwatch` at `[StarMapAllModsLoaded]`, so the
  first `Update` receives the whole load time; with auto save enabled in the settings file a save can be
  due on the very first UI frame (derived from the code, not tested in game).
- **Standalone host and hidden HUD.** Only the supermod replays `Update` while the HUD is hidden. The
  standalone host gets no `Update` with F2 on, but its `Stopwatch` keeps running, so the next visible frame
  adds the whole hidden period and saves immediately if it exceeded the interval.
- **Exceptions are contained.** `UniverseData.Create` throws if the main viewport has no camera, and
  `Populate` is outside `Make`'s write `try`; `AutoSaveController.SaveNow` catches and shows the message
  instead of letting it reach the frame.[^ksa-universedata][^sy-controller]
- **Semantic drift to watch** (authors' list): `Make` becoming `void` or `TryAccept` changing shape breaks
  the build; `Make` stopping to refresh the save list, KSA adding its own overwrite confirmation inside
  `Make`, or `RefusedInEditor` gaining conditions that `CanSaveNow` does not mirror would not.[^scope-game-integration]
- **Stale comments.** The XML doc on `AutoSaveSettings` still calls it "the exact payload of the version-1
  `save-yourself` scene record", and the status line says "this scene"; there is no such record and the
  counter is only reset on dispose.[^sy-settings]
- **Test coverage.** `save-yourself.tests` links the four production files (`AutoSaveSettings`,
  `AutoSaveNaming`, `AutoSaveController`, `AutoSaveSettingsStore`) into a KSA-free console project and
  stubs only `KsaPaths`. It covers naming, sanitising, clamping, countdown, waiting, refused and throwing
  writes and settings-file robustness; actual KSA saving and the sidecar are in-game acceptance items.[^sy-tests-csproj][^sy-tests-readme]

## See also

- [Native save and load hooks](./lib-native-save-hooks.md) (what fires for every save, including these)
- [Per-save scene sidecar](./unscience-scene-save-sidecar.md) (the `unscience.json` written with each auto save)
- [Hidden-UI frame hook](./lib-hidden-ui-frame-hook.md) (why the timer keeps running with F2)
- [Managed test harnesses](./unscience-managed-test-harnesses.md)
- [Save Yourself sub-mod](../submods/save-yourself.md) and [mod overview](../overview.md)
- [Write timed native saves with SaveName.TryAccept and UncompressedSave.Make](../../../howto/persistence/saves/save-and-load-the-game-from-code/save-and-load-the-game-from-code-1.md) - how-to article demonstrating this feature

[^sy-readme]: save-yourself/README.md
[^game-save-provider]: ksa-abstractions.lib/GameSaveProvider.cs
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-universedata]: KSA.UniverseData (decompiled, build 2026.10.10.5554)
[^sy-submod-ui]: save-yourself.lib/SaveYourselfSubmod.Ui.cs
[^ksa-savename]: KSA.SaveName (decompiled, build 2026.10.10.5554)
[^sy-store]: save-yourself.lib/AutoSaveSettingsStore.cs
[^ksa-paths]: ksa-abstractions.lib/KsaPaths.cs
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^scope-saves]: scope/saves.md
[^native-save-hooks]: ksa-abstractions.lib/Persistence/NativeSaveHooks.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^unscience-mod]: unscience/Mod.cs
[^hidden-ui-frame-hook]: ksa-abstractions.lib/HiddenUiFrameHook.cs
[^sy-controller]: save-yourself.lib/AutoSaveController.cs
[^scope-game-integration]: scope/game-integration-surface.md
[^sy-settings]: save-yourself.lib/AutoSaveSettings.cs
[^sy-tests-csproj]: save-yourself.tests/save-yourself.tests.csproj
[^sy-tests-readme]: save-yourself.tests/README.md
