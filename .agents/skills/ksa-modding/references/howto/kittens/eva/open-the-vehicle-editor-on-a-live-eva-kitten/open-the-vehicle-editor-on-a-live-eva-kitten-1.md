---
type: How-To
title: Open the editor on a live kitten with Program.EditorFlag at the handoff and root guards on every editor entry point
description: Set Program.EditorFlag from the PrepareFrame handoff so the stock existing-vehicle editor edits the controlled KittenEva in place, then guard the body root, new-vehicle, duplicate and blueprint paths so only the configured kitten is affected
category: kittens
group: eva
topic: open-the-vehicle-editor-on-a-live-eva-kitten
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/iron-man-kitten-vehicle-editing]
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - unscience PhysicsFrameHook.Enqueue (sets Program.EditorFlag at the PrepareFrame handoff)
  - harmony-postfix KSA.VehicleEditor.OnFrame
  - harmony-prefix KSA.VehicleEditor.OnMouseButton
  - harmony-prefix KSA.VehicleEditor.OnKey
  - harmony-prefix KSA.VehicleEditor.UpdateSelected
  - harmony-prefix KSA.VehicleEditor.DeletePart
  - harmony-prefix KSA.VehicleEditor.SetFocusedTree
  - harmony-prefix KSA.VehicleEditor.DuplicateHighlightedPart (private, by name)
  - harmony-prefix KSA.VehicleEditor.RequestNewVehicle (private, by name)
  - harmony-prefix KSA.VehicleEditor.FinalizeNewVehicle (private, by name)
  - harmony-postfix KSA.VehicleSaveData.Create(string, PartTree)
lifecycle: [frame:draw-ui, frame:prepare, frame:viewports, frame:input, editor:on-frame]
game_types:
  - KSA.Program
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.VehicleSaveData
  - KSA.KittenEva
  - KSA.Part
  - KSA.PartTree
tags: [kitten, eva, vehicle-editor, harmony, editor-flag, iron-man]
---
The stock vehicle editor can already edit a vessel in place, but KSA never offers it for a kitten: the debug-menu **Editor Toggle** is disabled while the controlled vehicle is a `KittenEva`. This article opens the editor on the live kitten itself - same `KittenEva` object, same character, same `PartTree` - lets the player bolt tanks, engines and RCS onto it, and returns to flight with the kitten intact. The mechanism is one flag, `Program.EditorFlag`, set from the physics handoff, plus a family of Harmony guards that keep the editor away from the body root. Prerequisites: StarMap, Harmony ([skip the original with a prefix](../../../patching/harmony/skip-the-original-method-with-a-prefix/overview.md)) and a way to run code at the handoff ([mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)). Demonstrated by Unscience's Iron Man sub-mod: [Open the stock vehicle editor on a live EVA kitten](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md). The two companion sub-steps are [approach 2](open-the-vehicle-editor-on-a-live-eva-kitten-2.md) (drawing the kitten upright in the editor) and [approach 3](open-the-vehicle-editor-on-a-live-eva-kitten-3.md) (private attachment nodes).

## Background: how the game does it

The editor is a plain object owned by `Program`, opened and closed by a boolean. The debug menu shows what the game refuses to do for a kitten:

```csharp
// current/decomp/KSA/Program.cs L3966-L3979
						ImString label16 = "Editor Toggle"u8;
						flag4 = EditorFlag;
						flag = Editor != null || !(ControlledVehicle is KittenEva);
						if (ImGui.MenuItem(label16, default(ImString), flag4, flag))
						{
							if (EditorFlag && Editor != null)
							{
								Editor.RequestExit();
							}
							else
							{
								EditorFlag = !EditorFlag;
							}
						}
```

`flag` is the menu item's enabled state: it is `true` when an editor already exists or when the controlled vehicle is not a `KittenEva`. Nothing else in the editor path refuses a kitten. Once `EditorFlag` is `true` and no editor exists, `Program.OnFrameEditor` builds one on whatever vehicle is controlled and parks the vehicle:

```csharp
// current/decomp/KSA/Program.cs L2567-L2578
	private void OnFrameEditor(double dtPlayer)
	{
		if (EditorFlag && Editor == null)
		{
			Editor = new VehicleEditor(ControlledVehicle, newVehicle: false);
			ControlledVehicle = null;
			Universe.SetSimulationSpeed(0.0);
		}
		Profiler.MainThread.Begin(ProfilerOnFrameTag);
		Editor?.OnFrame(MainViewport, dtPlayer);
		Profiler.MainThread.End();
	}
```

Passing an existing vehicle makes `VehicleEditor` store it as `ExistingVehicle` and build a `VehicleEditingSpace` over the vehicle's own `PartTree`, so edits happen in place ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1234-L1236), [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L91-L94)):

Leaving is a two-step handshake. `VehicleEditor.RequestExit` clears the flag after any unsaved-change prompts; the next `PrepareFrame` disposes the editor, and `VehicleEditor.Dispose` writes the edited tree back and returns control:

```csharp
// current/decomp/KSA/VehicleEditor.cs L2226-L2233
	private void FinalizeExit(bool launchNewVehicle)
	{
		if (launchNewVehicle)
		{
			LaunchNewVehicle = true;
		}
		Program.EditorFlag = false;
	}
```

```csharp
// current/decomp/KSA/Program.cs L2182-L2188
		if (!EditorFlag && Editor != null)
		{
			Editor.Dispose();
			Editor = null;
			CrewAssignmentWindow.Close();
			VehicleSaves.Close();
		}
```

```csharp
// current/decomp/KSA/VehicleEditor.cs L2307-L2316
			if (EditingSpace.Parts != null && ExistingVehicle.Parts != EditingSpace.Parts)
			{
				ExistingVehicle.Parts = EditingSpace.Parts;
			}
			if (AutomaticResourceGroupMode)
			{
				ExistingVehicle.Parts.ResourceGroupList.CalculateStages(reconfigureTankContents: true);
			}
			ExistingVehicle.Parts.ResetCachedPosMatrixValues();
			ExistingVehicle.UpdateVehicleConfiguration();
```

```csharp
// current/decomp/KSA/VehicleEditor.cs L2339-L2345
			IFollowable existingVehicle = ExistingVehicle;
			IFollowable target = existingVehicle ?? _fallbackObject;
			if (ExistingVehicle != null)
			{
				Program.ControlledVehicle = ExistingVehicle;
			}
			Program.MainViewport.MapCamera.SetFollow(target, tidalLocking: false, changeControl: true, alert: false);
```

Because the `KittenEva` object is never replaced, its id, `Character` and orbit survive the round trip. What does not survive a naive attempt is the body root: the editor is built to delete, duplicate, re-root and re-launch part trees, and a kitten's root part is the backpack that its character simulation is built around. That is what the guards in the steps below are for.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Button click queues the open action | ImGui panel from `[StarMapAfterGui]` | `frame:draw-ui` |
| The action sets `Program.EditorFlag` | a queued callback at the `PrepareFrame` handoff (see [mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)) | `frame:prepare` |
| `new VehicleEditor(ControlledVehicle, false)` is built, `ControlledVehicle` becomes null, simulation speed 0 | `Program.OnFrameEditor`, same frame, after the handoff | `frame:viewports` |
| `Editor.OnFrame`, `UpdateSelected` | `VehicleEditor.OnFrame` from `OnFrameEditor` | `editor:on-frame` |
| `OnKey`, `OnMouseButton` | GLFW callbacks | `frame:input` |
| Editor disposed, tree written back | `Program.PrepareFrame`, right after `InputEvents.ApplyInputEvents` | `frame:input` |

Opening happens at the handoff because the open action also edits the part tree (in Iron Man it adds the default body nodes, recomputes derived data and calls `UpdateVehicleConfiguration`), and that must not race a vehicle worker. The flag alone is only a bool, but the same queued action does both jobs. The exit side needs no handoff of its own: `PrepareFrame` disposes the editor before `PartTree.FlushDirtyDerived` and `ExecuteNextVehicleSolvers`, so the first physics step after the editor already sees the new tree. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame order.

## Steps

1. **Queue the open action from the UI.** Run the click handler at the handoff, not inline in the ImGui callback, and refuse anything that would corrupt the session. Iron Man's `Queue` wraps every action: one pending action at a time, a liveness check on the kitten, exceptions turned into a status line.

```csharp
// iron-man.lib/IronManSubmod.cs L49-L69
    private void Queue(KittenEva kitten, Action action)
    {
        if (_pending) return;
        _pending = true;
        PhysicsFrameHook.Enqueue(() =>
        {
            _pending = false;
            if (_disposed) return;
            try
            {
                if (kitten.IsDisposed || !VehicleProvider.GetAllVehicles(includeDebris: true).Contains(kitten))
                    throw new InvalidOperationException("The selected kitten is no longer in the current system.");
                action();
            }
            catch (Exception ex)
            {
                _status = ex.Message;
                Console.WriteLine($"iron-man: {ex}");
            }
        });
    }
```

```csharp
// iron-man.lib/IronManSubmod.Ui.cs L50-L61
        if (!editing)
        {
            if (ImGui.Button("Edit this kitten"u8)) Queue(kitten, () => OpenEditor(kitten));
            if (rocket) DrawFlightControls(kitten);
        }
        else
        {
            if (ImGui.Button("Return to flight"u8))
                Program.Editor!.RequestExit(launchNewVehicle: false);
            DrawConnectors(kitten);
        }
        ImGui.EndDisabled();
```

2. **Set the flag.** `OpenEditor` checks that the kitten is the controlled vehicle, not on a ladder and not already in an editor (`CheckCanEnter`), prepares it (here: default body nodes, engines stopped), then sets the flag. The stock `OnFrameEditor` shown above does the rest.

```csharp
// iron-man.lib/IronManSubmod.cs L88-L96
    private static void CheckCanEnter(KittenEva kitten)
    {
        if (!IronManPatches.Ready)
            throw new InvalidOperationException("Iron Man integration is unavailable; see the game log.");
        if (Program.IsEditorOpen)
            throw new InvalidOperationException("Return to flight before changing modes.");
        if (kitten.LocomotionState.Mode == LocomotionMode.Ladder)
            throw new InvalidOperationException("Release the ladder before editing or entering Iron Man mode.");
    }
```

```csharp
// iron-man.lib/IronManSubmod.cs L130-L139
    private void OpenEditor(KittenEva kitten)
    {
        CheckCanEnter(kitten);
        if (Program.ControlledVehicle != kitten)
            throw new InvalidOperationException("Control this kitten in flight before opening the editor.");
        Configure(kitten);
        StopEngines(kitten);
        Program.EditorFlag = true;
        _status = "Editing the existing kitten. Return to flight applies the assembled equipment.";
    }
```

3. **Identify the kitten session in every guard.** All guards start with `GetKitten(editor)`, which returns a kitten only when the editor's `ExistingVehicle` is a live `KittenEva` the player explicitly opted in. Every other editor session (ordinary rockets, new vehicles) therefore runs the stock code. `IsBody` recognises the root and any sub-part whose `FullPart` is the root.

```csharp
// iron-man.lib/IronManEditorPatches.cs L67-L77
    private static KittenEva? GetKitten(VehicleEditor? editor)
    {
        return editor?.ExistingVehicle is KittenEva kitten && !kitten.IsDisposed
            && IronManSubmod.Instance?.IsConfigured(kitten) == true ? kitten : null;
    }

    private static bool IsBody(Part? part, KittenEva kitten)
    {
        return part != null && (ReferenceEquals(part, kitten.Parts.Root)
            || ReferenceEquals(part.FullPart, kitten.Parts.Root));
    }
```

4. **Guard the entry points.** The editor manipulates the existing `PartTree` in place; replacing the root keeps the `KittenEva` type but breaks the character simulation built around the backpack collider (the mod's own comment). These handlers keep the body root out of selection, deletion, focus changes, duplication and new-vehicle flows; accessories stay selectable and deletable:

```csharp
// iron-man.lib/IronManEditorPatches.cs L79-L113
    // The editor manipulates the existing PartTree in place. The character's collider/root must
    // remain its root: replacing it preserves KittenEva's type but corrupts character simulation.
    private static void ClearRootSelection(VehicleEditor __instance)
    {
        KittenEva? kitten = GetKitten(__instance);
        if (kitten == null) return;
        if (IsBody(__instance.Highlighted, kitten))
        {
            __instance.Highlighted!.Grabbed = false;
            __instance.Highlighted.Highlighted = false;
            __instance.Highlighted.Selected = false;
            __instance.Highlighted = null;
        }
        if (IsBody(__instance.Selected, kitten)) __instance.Selected = null;
    }

    private static bool AllowDelete(VehicleEditor __instance, Part part)
    {
        KittenEva? kitten = GetKitten(__instance);
        return kitten == null || !IsBody(part, kitten);
    }

    private static bool AllowFocus(VehicleEditor __instance, PartTree newFocus)
    {
        KittenEva? kitten = GetKitten(__instance);
        return kitten == null || ReferenceEquals(newFocus, kitten.Parts);
    }

    private static bool AllowDuplicate(VehicleEditor __instance)
    {
        KittenEva? kitten = GetKitten(__instance);
        return kitten == null || !IsBody(__instance.Highlighted, kitten);
    }

    private static bool AllowNewVehicle(VehicleEditor __instance) => GetKitten(__instance) == null;
```

5. **Install them as prefixes and postfixes.** Resolve each target by name with `AccessTools.Method` and throw `MissingMethodException` on a miss, so a game rename disables the whole feature instead of half-installing it. `ClearRootSelection` runs as a prefix on the input handlers and a postfix on `OnFrame`, so a body highlight created inside the frame is cleared before the player can grab it. The three targets that are private in the decompile (`DuplicateHighlightedPart`, `RequestNewVehicle`, `FinalizeNewVehicle`) are resolved by string name:

```csharp
// iron-man.lib/IronManEditorPatches.cs L17-L43
    public static void Apply(Harmony harmony)
    {
        if (Patches.Count != 0) return;
        try
        {
            Patch(harmony, typeof(SuperMeshRenderSystem), nameof(SuperMeshRenderSystem.ClearBuckets),
                nameof(RenderCharacter), postfix: true);
            Patch(harmony, typeof(VehicleEditor), nameof(VehicleEditor.OnFrame), nameof(ClearRootSelection), postfix: true);
            Patch(harmony, typeof(VehicleEditor), nameof(VehicleEditor.OnMouseButton), nameof(ClearRootSelection));
            Patch(harmony, typeof(VehicleEditor), nameof(VehicleEditor.OnKey), nameof(ClearRootSelection));
            Patch(harmony, typeof(VehicleEditor), nameof(VehicleEditor.UpdateSelected), nameof(ClearRootSelection));
            Patch(harmony, typeof(VehicleEditor), nameof(VehicleEditor.DeletePart), nameof(AllowDelete));
            Patch(harmony, typeof(VehicleEditor), nameof(VehicleEditor.SetFocusedTree), nameof(AllowFocus));
            Patch(harmony, typeof(VehicleEditor), "DuplicateHighlightedPart", nameof(AllowDuplicate));
            Patch(harmony, typeof(VehicleEditor), "FinalizeNewVehicle", nameof(AllowNewVehicle));
            Patch(harmony, typeof(VehicleEditor), "RequestNewVehicle", nameof(AllowNewVehicle));
            MethodInfo create = AccessTools.Method(typeof(VehicleSaveData), nameof(VehicleSaveData.Create),
                new[] { typeof(string), typeof(PartTree) })
                ?? throw new MissingMethodException(typeof(VehicleSaveData).FullName, nameof(VehicleSaveData.Create));
            Add(harmony, create, nameof(PreserveCharacter), postfix: true);
        }
        catch
        {
            Remove(harmony);
            throw;
        }
    }
```

```csharp
// iron-man.lib/IronManEditorPatches.cs L53-L65
    private static void Patch(Harmony harmony, Type type, string method, string patch, bool postfix = false)
    {
        MethodInfo target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
        Add(harmony, target, patch, postfix);
    }

    private static void Add(Harmony harmony, MethodInfo target, string patchName, bool postfix)
    {
        MethodInfo patch = AccessTools.Method(typeof(IronManEditorPatches), patchName)!;
        var method = new HarmonyMethod(patch);
        harmony.Patch(target, prefix: postfix ? null : method, postfix: postfix ? method : null);
        Patches.Add((target, patch));
    }
```

The patched game methods: `VehicleEditor.OnFrame`, `VehicleEditor.OnMouseButton`, `VehicleEditor.OnKey`, `VehicleEditor.UpdateSelected`, `VehicleEditor.DeletePart`, `VehicleEditor.SetFocusedTree`, `VehicleEditor.DuplicateHighlightedPart`, `VehicleEditor.RequestNewVehicle`, `VehicleEditor.FinalizeNewVehicle` (all in [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs)).

6. **Keep the character on blueprints.** The two-argument `VehicleSaveData.Create(string, PartTree)` calls `Create(id)` first, and that overload copies `Character` only when `id` names a live vehicle:

```csharp
// current/decomp/KSA/VehicleSaveData.cs L35-L53
	public static VehicleSaveData Create(string id)
	{
		VehicleSaveData vehicleSaveData = new VehicleSaveData();
		vehicleSaveData.Id = id;
		vehicleSaveData.IdHash = KeyHash.Make(id.AsSpan());
		CelestialSystem? currentSystem = Universe.CurrentSystem;
		if (currentSystem != null && currentSystem.All.TryGet(id, out Vehicle output))
		{
			VehicleData vehicleData = output.SerializeSave();
			vehicleSaveData.RootPartInstance = vehicleData.RootPartInstance;
			vehicleSaveData.SequenceEnvironments = vehicleData.SequenceEnvironments;
			vehicleSaveData.FuelLinks = vehicleData.FuelLinks;
			if (output is KittenEva kittenEva)
			{
				vehicleSaveData.Character = kittenEva.Character.Id;
			}
		}
		return vehicleSaveData;
	}
```

A blueprint saved from the editor therefore loses its character unless you restore it. Iron Man matches the saved tree's root against every live kitten's `Parts.Root` and never guesses for unrelated trees:

```csharp
// iron-man.lib/IronManEditorPatches.cs L115-L129
    private static void PreserveCharacter(PartTree tree, VehicleSaveData __result)
    {
        // Connector persistence must keep its character identity even after explicit flight
        // support is disabled. Match a live kitten's actual root, never a copied equipment tree.
        if (IronManConnectors.GetOwned(tree.Root).Count == 0) return;
        foreach (Vehicle vehicle in VehicleProvider.GetAllVehicles(includeDebris: true))
        {
            if (vehicle is KittenEva kitten && !kitten.IsDisposed
                && ReferenceEquals(tree.Root, kitten.Parts.Root))
            {
                __result.Character = kitten.Character.Id;
                return;
            }
        }
    }
```

7. **Return to flight.** Call `RequestExit(launchNewVehicle: false)` from the UI; the stock confirmation prompts, `FinalizeExit`, the `PrepareFrame` dispose and the write-back shown in the background section do the rest.

```csharp
// iron-man.lib/IronManSubmod.Ui.cs L57-L58
            if (ImGui.Button("Return to flight"u8))
                Program.Editor!.RequestExit(launchNewVehicle: false);
```

8. **Tear down with the editor open.** If your mod can unload while the editor is up, finish the existing-vessel transaction yourself while the guards still exist. `Dispose` runs outside the handoff, so it first waits for the solvers:

```csharp
// iron-man.lib/IronManSubmod.cs L141-L156
    public void Dispose()
    {
        if (_disposed) return;
        // Lifecycle teardown is outside the normal queued handoff: wait before touching modules.
        JobSystems.VehicleSolver.Wait();
        JobSystems.ClothSolvers.Wait();
        if (Program.Editor?.ExistingVehicle is KittenEva editing && IsConfigured(editing))
        {
            // Finish the existing-vessel transaction while the avatar and root guards still
            // exist. This applies the edited tree just as the stock frame-loop teardown does.
            Program.Editor.Dispose();
            Program.Editor = null;
            Program.EditorFlag = false;
            CrewAssignmentWindow.Close();
            VehicleSaves.Close();
        }
```

9. **Check the result.** Open the panel on a controlled, non-ladder kitten and press **Edit this kitten**: the stock editor opens on the kitten's tree (the kitten is not drawn until [approach 2](open-the-vehicle-editor-on-a-live-eva-kitten-2.md)). Select the body root: it never highlights. Press **Return to flight**: the kitten is controlled again with the new parts.

## Complete example

- [iron-man.lib/IronManEditorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEditorPatches.cs) - the guards, the blueprint postfix and the avatar draw.
- [iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs) - `OpenEditor`, `Queue`, `Dispose`.
- [iron-man.lib/IronManSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.Ui.cs) - the Edit and Return buttons.
- Feature page: [Open the stock vehicle editor on a live EVA kitten](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md).

## Variations and gotchas

- **Edit an existing kitten only.** Launching an EVA blueprint from an empty editor still goes through `Vehicle.CreateVehicle` in `VehicleEditor.Dispose` and produces a plain `Vehicle`, so the new-vehicle path is blocked rather than extended.
- **Ladder kittens are refused.** The character is attached to a host vessel and the editor would edit a tree the ladder solver still owns.
- **No undo.** The editor has no undo history; connector edits in Iron Man set `HasUnsavedChanges` and `IsChangeStartOrEnd` so the stock exit prompts see them.
- **Reflection by name.** `AccessTools` returns null on a rename; install fails loudly with `MissingMethodException` and rolls back every patch. All targets exist in build 2026.10.10.5554.
- **The Editor Toggle stays disabled.** Iron Man does not unlock the debug-menu item; the mod button is the only entry.
- **Teardown order.** Remove the guards only after the editor is disposed; otherwise the stock dispose runs against an unguarded body.

## See also

- [Approach 2: draw the kitten upright in the editor](open-the-vehicle-editor-on-a-live-eva-kitten-2.md), [approach 3: private attachment nodes](open-the-vehicle-editor-on-a-live-eva-kitten-3.md), [topic overview](overview.md)
- Mod features: [Iron Man kitten vehicle editing](../../../../mods/unscience/features/iron-man-kitten-vehicle-editing.md), [Iron Man connector serialization](../../../../mods/unscience/features/iron-man-connector-serialization.md)
- [Fly an EVA kitten as an ordinary rocket vessel](../treat-an-eva-kitten-as-a-rocket/overview.md), [Reach a kitten's avatar and skeleton](../../characters/reach-a-kittens-avatar-and-skeleton/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
