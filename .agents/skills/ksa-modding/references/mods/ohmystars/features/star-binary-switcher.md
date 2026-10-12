---
type: Mod Feature
title: Star binary switcher
description: Discover star binary files, rewrite Mod.StarBinaries by reflection and reload the instanced star field at runtime with capacity-aware sampling, named presets and game-styled confirmation popups
mod: ohmystars
category: celestials
techniques: [reflection, imgui-window, ini-config]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - reflection KSA.ModLibrary.Lookup
  - reflection KSA.Mod.StarBinaries
  - reflection KSA.Program._starStarTechnique
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.ModLibrary
  - KSA.Mod
  - KSA.Program
  - KSA.InstancedStarTechnique
  - KSA.ViewportRegistry
  - KSA.Popup
  - KSA.TimedAlert
tags: [stars, star-binary, reflection, modlibrary, presets, popup, ini]
---

The lower half of the Star Editor lists every star binary (`*.bin`) found in the mod's own folder
and in the game's `Content/Core` folder, lets the player tick any combination, and reloads the
game's star field from the ticked files without restarting. Named presets (sliders plus selected
binaries) are saved to `starsedit_settings.ini`, and game-styled confirmation popups guard the
destructive buttons.[^oms-starseditwindow-cs]

## How it works

1. **Find the loaded mods.** `GetAllLoadedMods` reads the `internal static` field
   `ModLibrary.Lookup` by reflection and calls its `GetList()` method
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1041-L1057), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L66)).[^ksa-modlibrary-cs]
2. **Read and write `Mod.StarBinaries`.** The `[TomlField("starBinaries")] private string[]
   StarBinaries` field of each `Mod` is read and replaced through reflection
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1059-L1080), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L68-L69)).[^ksa-mod-cs]
   A snapshot of every mod's original list is taken at `[StarMapAllModsLoaded]` for "Game Default".
3. **Discover files.** `RebuildEntriesFromMods` scans the mod's own directory recursively for
   `*.bin`, finds the mod that owns `Content/Core` (first by a `StarBinaries` entry starting with
   `Content/Core/`, then by folder, then by name) and scans that folder's top level. An entry is
   pre-ticked if its path or file name is in some mod's current `StarBinaries`
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L516-L692)).
4. **Apply.** `ApplySelection` writes the ticked paths back into `StarBinaries` for the two owning
   mods, calls `starTechnique.ResetInstances(viewport)` for every viewport in
   `ViewportRegistry.Views`, loads the files itself, and finally `UpdateInstanceBuffer(viewport, 0)`
   for every viewport ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L694-L781),
   [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L919-L922)). The technique comes from the private static
   `Program._starStarTechnique` ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L783-L787),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L207)).[^ksa-program-cs]
5. **Capacity and quotas.** Each file's header gives its star count (capped by file length / 16).
   The mod requests `total + 1000` instances and re-creates the buffer whenever that differs from
   the current capacity, which the first apply of a session always does because the stock capacity is
   the header counts plus 200 (see [Star instance restyle patch](star-instance-restyle-patch.md)). If
   the capacity is still smaller than the total,
   it splits the capacity proportionally with a largest-remainder pass and, per file, picks evenly
   spaced records with a fractional stride ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L789-L998)).
6. **Load like the game.** Each picked record is passed to
   `starTechnique.AddInstance(Program.MainViewport, forward, scale, new byte4(r, g, b, 255), 19.5f)`,
   the same call and distance `Mod.LoadStarBinaries` uses
   ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)).
7. **Presets.** `starsedit_settings.ini` has a `[Sliders]` block (slider values and
   `Binary=<ModName>|<relative path>` lines) and one `[Saved:<name>]` block per preset
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1146-L1534)). The whole file is rewritten only
   when it is first created, when a preset is saved or used (which also makes it the `[Sliders]`
   block) and when a preset is deleted; moving a slider or pressing Apply writes nothing
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1196-L1220),
   [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1317-L1346)). Start-up reads only the
   `[Sliders]` values; the `Binary=` lines are used by "Restore Settings > LAST USED", which, like a
   preset, ticks the listed entries and applies them. "INITIAL LOAD" restores what was loaded this
   session.
8. **Game-styled popups.** `ChoicePopup` subclasses the game's abstract `Popup`, draws with
   `BeginConsoleModal`, `Popup.CenteredWrappedText` and `Popup.DrawConsoleButtonRow` (detailed in
   [Game-styled popups and toasts](game-styled-popups-and-toasts.md)), and is shown
   simply by constructing it, because the `Popup` constructor registers the instance in the list
   `Popup.DrawAll` draws each frame ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L26-L63),
   [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L46-L72)).[^ksa-popup-cs] `TimedAlert.Create` and
   `CreateWarning` give feedback ([current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L69-L75)).

## Lifecycle and timing

- **`startup:all-mods-loaded`.** `StarsEditWindow.OnAllModsLoaded` snapshots `StarBinaries`, resolves
  the mod entry by folder, creates or loads the ini, and builds the list; the star technique does
  not exist yet (it is created later in the `Program` constructor), so the first apply is deferred
  with `_pendingInitialSettingsApply` ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L169-L181)).
- **`frame:draw-ui`.** The window is drawn from `[StarMapBeforeGui]` (prefix on
  `Program.OnDrawUiFrame`); the deferred apply runs on the first frame the window is open and
  `GetStarTechnique()` is non-null ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L206-L212)). Applies run on the
  main thread before this frame's render records the star draw, so the new field shows in the same
  frame. Popups are drawn by `Popup.DrawAll` inside `OnDrawUiFrame`, right after the mod's windows.

## Key code

| Path | Role |
|---|---|
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L516-L692) | Discovery of mod and Core binaries |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L694-L787) | Apply: write `StarBinaries`, reset, reload, upload |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L789-L998) | Quotas and stride sampling loader |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1041-L1080) | Reflection on `ModLibrary.Lookup` and `Mod.StarBinaries` |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1146-L1534) | Preset ini read/write |

## Snippets

```csharp
// src/StarsEditWindow.cs L963-L998
		int declaredTotal = reader.ReadInt32();
		long maxByLength = (reader.BaseStream.Length - sizeof(int)) / (sizeof(float) * 3 + sizeof(byte) * 4);
		int total = (int)Math.Min(Math.Max(declaredTotal, 0), Math.Max(maxByLength, 0));
		if (total <= 0)
		{
			return 0;
		}

		int target = Math.Min(quota, total);
		double step = (double)total / target;
		double nextPick = 0d;
		int picked = 0;

		for (int index = 0; index < total; index++)
		{
			if (reader.BaseStream.Position + (sizeof(float) * 3 + sizeof(byte) * 4) > reader.BaseStream.Length)
			{
				break;
			}

			float3 forward = new float3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			byte scale = reader.ReadByte();
			byte r = reader.ReadByte();
			byte g = reader.ReadByte();
			byte b = reader.ReadByte();

			if (picked < target && index >= (int)Math.Floor(nextPick))
			{
				starTechnique.AddInstance(Program.MainViewport, forward, scale, new byte4(r, g, b, byte.MaxValue), 19.5f);
				picked++;
				nextPick += step;
			}
		}

		return picked;
	}
```

```csharp
// src/StarsEditWindow.cs L1041-L1080
	private static List<Mod> GetAllLoadedMods()
	{
		_lookupField ??= typeof(ModLibrary).GetField("Lookup", BindingFlags.NonPublic | BindingFlags.Static);
		object? lookup = _lookupField?.GetValue(null);
		if (lookup is null)
		{
			return new List<Mod>();
		}

		_lookupGetListMethod ??= lookup.GetType().GetMethod("GetList", BindingFlags.Public | BindingFlags.Instance);
		if (_lookupGetListMethod?.Invoke(lookup, null) is IEnumerable<Mod> mods)
		{
			return mods.Where(static m => m is not null).ToList();
		}

		return new List<Mod>();
	}

	private static string[] GetStarBinariesForMod(Mod mod)
	{
		_starBinariesField ??= typeof(Mod).GetField("StarBinaries", AnyInstance);
		if (_starBinariesField?.GetValue(mod) is string[] current)
		{
			return current;
		}

		return Array.Empty<string>();
	}

	private static bool SetStarBinariesForMod(Mod mod, string[] selectedBinaries)
	{
		_starBinariesField ??= typeof(Mod).GetField("StarBinaries", AnyInstance);
		if (_starBinariesField is null)
		{
			return false;
		}

		_starBinariesField.SetValue(mod, selectedBinaries);
		return true;
	}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ModLibrary.Lookup` (internal) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L66) | All loaded mods |
| `Mod.StarBinaries` (private) | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L68-L69) | Per-mod star file list |
| `Mod.LoadStarBinaries` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285) | Record format and load call to imitate |
| `Program._starStarTechnique` (private static) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L207) | The star technique |
| `InstancedRenderTechnique<T>.ResetInstances` | [current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L100-L103) | Clear before reload |
| `InstancedStarTechnique.UpdateInstanceBuffer` | [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L43-L49) | Upload to the mapped buffer |
| `ViewportRegistry.Views` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L48) | Every viewport slot |
| `Popup`, `PopupButtonKind` | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L12-L72) | Confirmation modals |
| `TimedAlert.Create`, `CreateWarning` | [current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L69-L75) | Toasts |

## Pitfalls and version notes

- **Other mods' stars vanish on Apply.** `ResetInstances` clears every star, but only the mod's own
  folder and Core are scanned and reloaded; star binaries contributed by any third mod's
  `starBinaries` are not listed and are not reloaded.
- **A saved binary selection is never restored at start-up.** The game loads the binaries listed in
  each `mod.toml`, `RebuildEntriesFromMods` ticks exactly those, and the deferred first apply (which
  waits until the Star Editor window is drawn; the shipped `settings.ini` has `ShowStarEditor=False`)
  reloads the same stock selection. The player has to pick "Restore Settings > LAST USED" or a preset
  each session. Saved slider values do apply at start-up, through the prefix.
- **Reflection on private members.** `ModLibrary.Lookup`, `Mod.StarBinaries`,
  `Program._starStarTechnique` and the technique's buffer fields are all non-public and can change
  in any game update; each lookup degrades to a status message rather than an exception.
- **The overlay's star filter is not updated.** [Stellarium sky cultures](stellarium-sky-cultures.md)
  still filters asterisms against the stock Core binary.
- **No normalisation.** The mod passes the raw `forward` from the file; `AddInstance` normalises it
  anyway (`NormalizeOrZero`), so this is harmless.

## See also

- [Star instance restyle patch](star-instance-restyle-patch.md)
- [Game-styled popups and toasts](game-styled-popups-and-toasts.md)
- [settings.ini persistence](ini-settings-persistence.md)
- [ModMenu entry and game-styled windows](modmenu-entry-and-console-style-windows.md)
- [Mirror Mod.LoadStarBinaries when parsing](../../../howto/celestials/systems-and-stars/read-and-replace-star-binaries/read-and-replace-star-binaries-1.md) - how-to article demonstrating this feature
- [Reflect Mod.StarBinaries, reset and refill the star technique, confirm through a game Popup](../../../howto/celestials/systems-and-stars/read-and-replace-star-binaries/read-and-replace-star-binaries-2.md) - how-to article demonstrating this feature
- [Popups: a Popup subclass that registers itself and is drawn by Popup.DrawAll](../../../howto/ui/windows/show-alerts-and-popups-in-game/show-alerts-and-popups-in-game-2.md) - how-to article demonstrating this feature

[^oms-starseditwindow-cs]: OhMyStars src/StarsEditWindow.cs
[^ksa-modlibrary-cs]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-mod-cs]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-popup-cs]: KSA.Popup (decompiled, build 2026.10.10.5554)
