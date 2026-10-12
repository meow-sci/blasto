---
type: How-To
title: Reflect Mod.StarBinaries, reset and refill the star technique, confirm through a game Popup
description: Switch the star field at runtime by listing star binaries in every loaded mod, rewriting the private StarBinaries, resetting every viewport's star instances, refilling within capacity by proportional stride sampling and uploading the instance buffer
category: celestials
group: systems-and-stars
topic: read-and-replace-star-binaries
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/star-binary-switcher]
techniques: [reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - reflection KSA.ModLibrary.Lookup
  - reflection KSA.Mod.StarBinaries
  - reflection KSA.Program._starStarTechnique
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.InstancedStarTechnique
  - KSA.Program
  - KSA.ViewportRegistry
  - KSA.Popup
  - KSA.TimedAlert
tags: [star-binary, reload, reflection, capacity, popup, presets]
---
Outcome: the lower half of a Star Editor window lists every star binary found in the mod's own folder and in Core's, lets the player tick any combination and reloads the game's star field without a restart; named presets are saved to an ini. Sub-step of the star-binaries topic: the runtime swap after the binaries can be read. Demonstrated by Oh My Stars ([Star binary switcher](../../../../mods/ohmystars/features/star-binary-switcher.md)).

## Background: how the game does it

The star field is `InstancedStarTechnique` instances filled once by `ModLibrary.PopulateStars` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L561-L573)) from each mod's `StarBinaries`; the buffer holds `GetStarCount()` instances (200 plus the header counts). `ResetInstances(viewport)` then `AddInstance` then `UpdateInstanceBuffer(viewport, 0)` is the cycle. The pieces a mod needs are all non-public: `ModLibrary.Lookup` is an internal static collection with a public `GetList()`, `Mod.StarBinaries` is a private field, `Program._starStarTechnique` a private static field. When the replacement set is larger than the stock capacity, the buffer must grow (the technique's `_maxInstances`, `Instances` and `CreateInstanceBuffer` are private too; see [grow game GPU buffers](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md) for the general pattern).

## Lifecycle and timing

- `startup:all-mods-loaded`: snapshot every mod's original `StarBinaries` (for "Game Default"), resolve the owning mod entries and build the file list. The star technique does not exist yet (it is created later in the `Program` constructor, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1049)), so the first apply is deferred until it is non-null.
- `frame:draw-ui`: the window is drawn from `[StarMapBeforeGui]`; applies run on the main thread before this frame's render records the star draw, so the new field shows in the same frame. Popups are drawn by `Popup.DrawAll` inside `OnDrawUiFrame`.

See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **List loaded mods.** `ModLibrary.Lookup` is internal: read the field by reflection and call its public `GetList()`:

```csharp
// src/StarsEditWindow.cs L1041-L1057
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
```

2. **Read and replace `Mod.StarBinaries`** (a `[TomlField("starBinaries")] private string[]`) by reflection, and find the technique through `Program._starStarTechnique`:

```csharp
// src/StarsEditWindow.cs L1059-L1080
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

3. **Discover files:** scan the mod's folder recursively for `*.bin`, find the mod that owns `Content/Core` (first by a `StarBinaries` entry starting with `Content/Core/`, then by folder, then by name) and scan its top level; pre-tick any entry already in some mod's list.

4. **Apply:** write the ticked paths back into the two owning mods, reset every viewport, load, and upload for every viewport:

```csharp
// src/StarsEditWindow.cs L740-L756
int selectedTotal = 0;
foreach (KeyValuePair<Mod, List<string>> kvp in selectedByMod)
{
	string[] selected = kvp.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
	if (!SetStarBinariesForMod(kvp.Key, selected))
	{
		_status = $"Cannot apply: failed to update StarBinaries for mod '{GetModLabel(kvp.Key)}'.";
		return;
	}

	selectedTotal += selected.Length;
}

foreach (IViewport viewport in ViewportRegistry.Views)
{
	starTechnique.ResetInstances(viewport);
}
```

5. **Respect the capacity.** Request `total + 1000` instances and, when that differs from the current capacity, rebuild the technique's buffer (unmap and dispose the old buffer, resize the per-viewport CPU arrays, set `_maxInstances`, call the private `CreateInstanceBuffer`):

```csharp
// src/StarsEditPatcher.cs L207-L224
object? oldMemory = _instanceMemoryField?.GetValue(starTechnique);
oldMemory?.GetType().GetMethod("Unmap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(oldMemory, null);

object? oldBuffer = _instanceBufferField?.GetValue(starTechnique);
if (oldBuffer is IDisposable disposableBuffer)
{
    disposableBuffer.Dispose();
}
else
{
    oldBuffer?.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(oldBuffer, null);
}

_maxInstancesField.SetValue(starTechnique, requestedCapacity);
_instancesField.SetValue(starTechnique, resized);
_createInstanceBufferMethod.Invoke(starTechnique, null);

int adapted = GetMaxInstances(starTechnique);
```

6. **Fit the quota.** If the capacity is still smaller than the total, split it proportionally with a largest-remainder pass and, per file, take evenly spaced records with a fractional stride:

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

7. **Upload for every viewport:**

```csharp
// src/StarsEditWindow.cs L919-L922
foreach (IViewport viewport in ViewportRegistry.Views)
{
	starTechnique.UpdateInstanceBuffer(viewport, 0);
}
```

8. **Confirm destructive actions with a game-styled popup.** `ChoicePopup` subclasses the game's abstract `Popup`; constructing it registers it in the list `Popup.DrawAll` draws each frame; `TimedAlert.Create` and `CreateWarning` give feedback ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L26-L63), [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L46-L72), [current/decomp/KSA/TimedAlert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TimedAlert.cs#L69-L75)). See [show alerts and popups in game](../../../ui/windows/show-alerts-and-popups-in-game/overview.md).

9. **Save named presets** to an ini (`[Sliders]` plus one `[Saved:<name>]` block per preset with `Binary=<ModName>|<relative path>` lines); start-up reads only the slider values, and "Restore Settings > LAST USED" ticks and applies the listed entries.

## Complete example

| File | Role |
|---|---|
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L694-L787) | Apply: write `StarBinaries`, reset, reload, upload |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L789-L998) | Quotas and stride sampling loader |
| [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L159-L247) | Runtime capacity adaptation |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1146-L1534) | Preset ini read and write |

## Variations and gotchas

- **Private names.** `ModLibrary.Lookup`, `Mod.StarBinaries`, `Program._starStarTechnique`, `InstancedRenderTechnique._maxInstances`, `Instances` and `CreateInstanceBuffer` can change in an update; every lookup logs a status instead of throwing.
- **`AddInstance` throws** "Instance buffer full" beyond capacity; always cap (the mod also patches the add path to drop and count overflow while applying).
- **Instances are added for the main viewport only**, as `Mod.LoadStarBinaries` does; reset and upload every viewport.
- **No persistence in the game:** a rewritten `StarBinaries` lasts for the session; apply the saved preset again on the next start.

## See also

- [Overview](overview.md); [approach 1](read-and-replace-star-binaries-1.md)
- [Star binary switcher](../../../../mods/ohmystars/features/star-binary-switcher.md)
- [Grow game GPU buffers for runtime content](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md), [Show alerts and popups in game](../../../ui/windows/show-alerts-and-popups-in-game/overview.md)
