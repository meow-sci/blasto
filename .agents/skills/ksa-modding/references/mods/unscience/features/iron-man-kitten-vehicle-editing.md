---
type: Mod Feature
title: Open the stock vehicle editor on a live EVA kitten
description: Iron Man sets Program.EditorFlag from the physics handoff so the stock editor opens on the controlled KittenEva in place, then guards the body root with editor prefixes, draws the avatar into the editor prepass and rotates the editor camera so the kitten stands upright
mod: unscience
submod: iron-man
category: kittens
techniques: [harmony-prefix, harmony-postfix, harmony-transpiler, reflection]
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
  - harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets
  - harmony-postfix KSA.OrbitController.GetFrame2Ecl (private, by name)
  - harmony-transpiler KSA.OrbitController.EditorOnScroll (private, by name)
lifecycle: [frame:prepare, frame:input, frame:viewports, editor:on-frame, editor:render]
game_types:
  - KSA.Program
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.VehicleSaveData
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.SuperMeshRenderSystem
  - KSA.OrbitController
  - KSA.Part
  - KSA.PartTree
tags: [eva, kitten, vehicle-editor, harmony, transpiler, camera, avatar-rendering]
---

KSA greys out the **Editor Toggle** debug menu item while the controlled vehicle is a `KittenEva`,
so an EVA kitten normally cannot be refitted. Iron Man adds an **Edit this kitten** button that opens
the stock vehicle editor on the live kitten itself: the same `KittenEva` object, its character, orbit
and part tree. The player attaches tanks, engines and RCS from the stock part browser onto
instance-owned body nodes, then returns to flight with the same kitten.[^iron-man-readme]

Mechanism in one line: set the public `Program.EditorFlag` at the physics handoff and let the stock
existing-vehicle editor path run, then patch the few places where an EVA body breaks that path (root
selection, delete, duplicate, new vehicle, blueprint metadata, avatar rendering and camera basis).

## How it works

**Opening.** `OpenEditor` runs as a queued `PhysicsFrameHook` action. It refuses when the
integration failed to install, when the editor is already open, when the kitten is on a ladder or is
not the controlled vehicle. It then configures the kitten (adds the default Up/Down body nodes, see
[Iron Man connector serialization](./iron-man-connector-serialization.md)), stops and disarms
engines, and sets `Program.EditorFlag = true`
([iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs#L88-L139)).
The game does the rest: `Program.OnFrameEditor` sees the flag with no editor and constructs
`new VehicleEditor(ControlledVehicle, newVehicle: false)`.[^ksa-program] For an existing vehicle the
editor stores it as `ExistingVehicle` and builds a `VehicleEditingSpace` whose `Parts` is the
kitten's own `PartTree`, so edits happen in place.[^ksa-vehicle-editor][^ksa-vehicle-editing-space]

**Closing.** **Return to flight** calls `Program.Editor.RequestExit(launchNewVehicle: false)`
([iron-man.lib/IronManSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.Ui.cs#L50-L60)).
After the stock confirmation prompts, `FinalizeExit` clears `Program.EditorFlag`; the next
`PrepareFrame` disposes the editor, and `VehicleEditor.Dispose` writes `EditingSpace.Parts` back to
`ExistingVehicle.Parts`, calls `UpdateVehicleConfiguration` and restores
`Program.ControlledVehicle`.[^ksa-vehicle-editor] The `KittenEva` type, id and `Character` survive
because the object is never replaced.

**Body protection** ([iron-man.lib/IronManEditorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEditorPatches.cs#L17-L113)).
All guards first call `GetKitten(editor)`, which returns a kitten only when
`editor.ExistingVehicle` is a live `KittenEva` that the player explicitly configured, so every other
editor session is untouched.

| Patch | Effect for a configured kitten |
|---|---|
| postfix `OnFrame`, prefixes `OnMouseButton`, `OnKey`, `UpdateSelected` | Clear `Highlighted`/`Selected`/`Grabbed` when they point at the body root (or a sub-part whose `FullPart` is the root); accessories stay selectable |
| prefix `DeletePart(Part)` | Skip when the part is the body |
| prefix `SetFocusedTree(PartTree)` | Only allow focusing the kitten's own tree |
| prefix private `DuplicateHighlightedPart` | Skip copying the body |
| prefixes private `RequestNewVehicle`, `FinalizeNewVehicle` | Block "new vehicle" while editing a kitten |
| postfix `VehicleSaveData.Create(string, PartTree)` | Put `Character` back on blueprints saved from a live authored kitten |

The comment in the code gives the reason for the root guard: the editor manipulates the existing
`PartTree` in place, and replacing the root keeps the `KittenEva` type but breaks the character
simulation built around the backpack collider. The blueprint postfix exists because the
two-argument `Create` only copies `Character` when the save id names a live vehicle; Iron Man matches
the tree's root against every live kitten's `Parts.Root` and never guesses for unrelated trees.

**Avatar in the editor.** The editor only draws part-tree geometry
(`Editor.EditingSpace.Parts?.UpdateRenderData` in `UpdateRenderingResources`), and the kitten body is
not part geometry, so without help the player would edit an invisible kitten. `RenderEditor` clears
the main viewport's SuperMesh buckets and immediately records the prepass. A postfix on
`SuperMeshRenderSystem.ClearBuckets` therefore submits the kitten's `KittenRenderable` right after the
clear, using `EditingSpace.GetMatrixAsmb2Ego` as the transform, `dt = 0` (no animation advance) and
`HideHead` forced off for the call.[^ksa-program][^ksa-supermesh] The assembly-to-ego matrix is passed
unmodified: `KittenEva.UpdateRenderData` subtracts the flight centre of mass from its own world
matrix, which the editor frame must not do again.[^ksa-kitten-eva]

**Upright camera** ([iron-man.lib/IronManEditorOrientationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEditorOrientationPatches.cs)).
Stock editor framing assumes a rocket: `OrbitController.GetFrame2Ecl` returns
`Concatenate(AngleAxis(UnitY, pi/2), EditingSpace.Asmb2Ecl)`, mapping camera +Z onto assembly +X, and
`EditorOnScroll` pans `CameraOffset` along assembly +X with bounds on `CameraOffset.X`.[^ksa-orbit-controller]
A kitten is authored feet at origin with -Z up, so it would lie on its side. Iron Man changes only
the camera basis (a postfix that returns `Concatenate(AngleAxis(UnitX, pi), Asmb2Ecl)` for the
configured editing space and `CameraReferenceFrame.Editor`) and rewrites the scroll handler with a
transpiler: the two `double3.UnitX` getter calls become a helper returning `-UnitZ`, and the two
`ldflda CameraOffset; ldfld X` pairs become `ldfld CameraOffset; call PanCoordinate`, which projects
the offset on the same up axis. The transpiler requires exactly two of each or throws. Geometry,
`Asmb2Ecl`, connectors, picking and gizmos are untouched, so the whole scene rotates together.

## Lifecycle and timing

| Step | Where (build 5554) | Lifecycle |
|---|---|---|
| Button click queues `OpenEditor` | ImGui panel, `ISubmod.RenderContent` | `frame:draw-ui` |
| `OpenEditor` sets `EditorFlag` | `PhysicsFrameHook` dispatcher, `Program.PrepareFrame` L2216 | `frame:prepare` |
| `new VehicleEditor(ControlledVehicle, false)`, `ControlledVehicle = null`, sim speed 0 | `Program.OnFrameEditor` L2567-L2578, called at L2262 the same frame | `frame:viewports` |
| `OnFrame` postfix, `UpdateSelected` prefix | `Editor.OnFrame(MainViewport, dt)` from `OnFrameEditor`; `UpdateSelected` from `OnFrame` (`VehicleEditor.cs` L2497) | `editor:on-frame` |
| `OnKey`, `OnMouseButton`, `EditorOnScroll` | GLFW callbacks during `Glfw.PollEvents` (`Program.cs` L2179) | `frame:input` |
| `GetFrame2Ecl` postfix | `OrbitController.OnFrame` (def L487, call at L614), from viewport `OnFrame` in `OnFrameViewports` (`Program.cs` L2622) | `frame:viewports` |
| `ClearBuckets` postfix draws the avatar | `Program.RenderEditor` L4984, prepass at L4987 | `editor:render` |
| Editor disposed, tree applied | `Program.PrepareFrame` L2182-L2189, after input, before the handoff | `frame:input` |

Why the handoff for opening: `OpenEditor` also adds connectors, recomputes derived data and calls
`UpdateVehicleConfiguration`, which must not race a vehicle worker; at L2216 the previous step is
applied and the next snapshot is not yet taken. Setting the flag there also means the editor is built
in the same frame. On exit, the stock dispose runs at L2182, before `PartTree.FlushDirtyDerived` and
`ExecuteNextVehicleSolvers`, so the first physics step after the editor already sees the new tree.

The avatar must be drawn after `ClearBuckets` and before `PrePassRenderer.Render`; a submission from
`StarMapAfterGui` would be erased by the clear (the code comment says exactly this). `ClearBuckets`
is also called per viewport from `RenderViewport` and `RenderGame`; the postfix returns unless the
renderer is `Program.Instance.SuperMeshRenderSystem`, the rendered viewport is the main viewport and
the editor is editing this kitten's tree.

Teardown: `IronManSubmod.Dispose` runs outside the handoff, so it waits on
`JobSystems.VehicleSolver` and `ClothSolvers`, then disposes an open kitten editor itself (the same
steps as `PrepareFrame` L2182-L2189) while the root guards still exist.

## Key code

| Path | Role |
|---|---|
| [iron-man.lib/IronManSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.cs#L130-L173) | `OpenEditor` (guards, configure, `EditorFlag`) and editor-aware `Dispose` |
| [iron-man.lib/IronManSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManSubmod.Ui.cs#L110-L190) | Edit/Return buttons, body-node editor, queued connector edits marking the tree dirty |
| [iron-man.lib/IronManEditorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEditorPatches.cs) | Root guards, blueprint `Character` postfix, editor avatar submission |
| [iron-man.lib/IronManEditorOrientationPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManEditorOrientationPatches.cs) | Upright camera basis postfix and scroll-pan transpiler |
| [iron-man.lib/IronManConnectors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectors.cs#L22-L28) | Default Up/Down body nodes created on first configure |

## Snippets

The stock path Iron Man relies on: a set flag with no editor opens one on the controlled vehicle.

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

Root guards; each one is a no-op unless the editor is editing a configured kitten:

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

Drawing the existing avatar after the editor's bucket clear, before the prepass:

```csharp
// iron-man.lib/IronManEditorPatches.cs L131-L153
    // Program.RenderEditor clears the immediate character buckets after ordinary part render
    // data is prepared. Submit here, before the prepass; submitting in AfterGui would be erased.
    private static void RenderCharacter(SuperMeshRenderSystem __instance)
    {
        VehicleEditor? editor = Program.Editor;
        KittenEva? kitten = GetKitten(editor);
        if (kitten == null || !ReferenceEquals(__instance, Program.Instance.SuperMeshRenderSystem)
            || !ReferenceEquals(Program.RenderedViewport, Program.MainViewport)
            || !ReferenceEquals(editor!.EditingSpace.Parts, kitten.Parts)) return;
        try
        {
            IViewport viewport = Program.RenderedViewport;
            float4x4 transform = float4x4.Pack(editor.EditingSpace.GetMatrixAsmb2Ego(viewport.GetCamera()));
            LocomotionState locomotion = kitten.LocomotionState;
            KittenAnimInputs inputs = default;
            bool hideHead = kitten.Renderable.HideHead;
            try
            {
                kitten.Renderable.HideHead = false;
                kitten.Renderable.UpdateRenderData(viewport, Program.Instance.ResourceFrameIndex, 0.0,
                    transform, double3.Zero, double3.Zero, in locomotion, in inputs);
            }
            finally { kitten.Renderable.HideHead = hideHead; }
```

Rotating only the camera basis so the kitten stands upright:

```csharp
// iron-man.lib/IronManEditorOrientationPatches.cs L59-L67
    private static void FramePostfix(IFollowable focused, CameraReferenceFrame referenceFrame, ref doubleQuat __result)
    {
        var space = GetEditingSpace();
        if (space == null || !ReferenceEquals(focused, space) || referenceFrame != CameraReferenceFrame.Editor) return;
        // Stock rotates camera-frame +Z to rocket assembly +X. A kitten's up is -Z.
        // Changing only this camera basis rotates the complete viewed assembly together;
        // Asmb2Ecl and every body, equipment, connector, picking and gizmo transform stay intact.
        __result = doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, Math.PI), space.Asmb2Ecl);
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.EditorFlag`, `Program.IsEditorOpen` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L225) | Open the editor; refuse mode changes while open |
| `Program.OnFrameEditor` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2567-L2578) | Builds `VehicleEditor(ControlledVehicle, newVehicle: false)` |
| Debug menu "Editor Toggle" | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3966-L3979) | Disabled for `KittenEva`; Iron Man does not unlock it |
| `Program.RenderEditor` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4980-L4987) | `ClearBuckets(RenderedViewport)` then `PrePassRenderer.Render` |
| `VehicleEditor.ExistingVehicle`, `EditingSpace`, `Highlighted`, `Selected`, `IsChangeStartOrEnd` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L543-L551) | Detect the kitten session, clear body selection, mark edits |
| `VehicleEditor.OnFrame`, `OnKey`, `OnMouseButton`, `UpdateSelected`, `DeletePart`, `SetFocusedTree` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2429) | Guarded entry points (L2429, L2645, L3485, L3777, L7031, L7003) |
| `VehicleEditor.DuplicateHighlightedPart`, `RequestNewVehicle`, `FinalizeNewVehicle` (private) | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L6048) | Blocked for the body (L6048, L2194, L2235) |
| `VehicleEditor.RequestExit`, `FinalizeExit`, `Dispose` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2181-L2343) | Exit transaction that writes `ExistingVehicle.Parts` and restores control |
| `VehicleEditor.HighlightConnector`, `CameraOffset` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2830) | Hover highlight of body nodes; pan offset read by the transpiler (L703) |
| `VehicleEditingSpace(Vehicle)`, `Asmb2Ecl`, `GetMatrixAsmb2Ego` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L91-L110) | Editing space shares the kitten's `PartTree`; avatar transform |
| `VehicleSaveData.Create(string, PartTree)` | [current/decomp/KSA/VehicleSaveData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaveData.cs#L35-L66) | Blueprint save; `Character` only set when the id names a live kitten |
| `SuperMeshRenderSystem.ClearBuckets(IViewport)` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L554) | Clears the view's prepass/opaque/translucency buckets; postfix seam |
| `KittenRenderable.UpdateRenderData(...)`, `HideHead` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311) | Draws body, fur, helmet, MMU into `ViewForViewport(viewport)` |
| `KittenEva.Renderable`, `KittenEva.UpdateRenderData` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | Flight path shifts by centre of mass; the editor call must not |
| `OrbitController.GetFrame2Ecl`, `EditorOnScroll` (private) | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L233) | Editor camera basis (L290-L293) and scroll pan (L433) |

## Pitfalls and version notes

- Private targets are resolved by name (`DuplicateHighlightedPart`, `RequestNewVehicle`,
  `FinalizeNewVehicle`, `GetFrame2Ecl`, `EditorOnScroll`); `AccessTools` returns null on a rename and
  `Apply` throws `MissingMethodException`, which rolls back every Iron Man patch. All exist in 5554.
- `SuperMeshRenderSystem.ClearBuckets` took no arguments before rev 5474 and is now
  `ClearBuckets(IViewport)`; the postfix binds only `__instance`, so it survived the change. It is
  called per viewport, hence the main-viewport filter.[^scope-iron-man]
- The scroll transpiler counts IL shapes (two `UnitX` getters, two `ldflda CameraOffset; ldfld X`
  pairs). A recompiled `EditorOnScroll` with a different shape makes installation fail, not misbehave.
- The workflow is "edit an existing kitten". Launching an EVA blueprint from an empty editor still
  goes through `Vehicle.CreateVehicle` in `VehicleEditor.Dispose` and produces a plain `Vehicle`;
  Iron Man does not replace stock spawning.[^iron-man-research]
- The game editor has no undo history; connector edits set `HasUnsavedChanges`,
  `PerformanceSequences.SetDirty()` and `IsChangeStartOrEnd` so the stock exit prompts and saves see
  them.
- Ladder kittens are refused: the character is attached to a host vessel and the editor would edit a
  tree the ladder solver still owns.
- The avatar call passes `dt = 0` and a default `KittenAnimInputs`; the pose is whatever the last
  flight frame left, and Kitten Animations' prefix still runs for it (see
  [forced clip playback](./kitten-animations-forced-clip-playback.md)).

## See also

- [Iron Man connector serialization](./iron-man-connector-serialization.md) for the body nodes the editor snaps to
- [Iron Man kitten rocket flight](./iron-man-kitten-rocket-flight.md) for the EVA/Iron Man mode switch
- [Iron Man equipment render ordering](./iron-man-equipment-render-ordering.md) for drawing the equipment in flight
- [Physics frame hook](./lib-physics-frame-hook.md) for the queued handoff
- [Iron Man sub-mod](../submods/iron-man.md) and [mod overview](../overview.md)
- [Open the editor on a live kitten with Program.EditorFlag at the handoff and root guards on every editor entry point](../../../howto/kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/open-the-vehicle-editor-on-a-live-eva-kitten-1.md) - how-to article demonstrating this feature
- [Draw the avatar from a ClearBuckets postfix and re-base the editor camera upright](../../../howto/kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/open-the-vehicle-editor-on-a-live-eva-kitten-2.md) - how-to article demonstrating this feature

[^iron-man-readme]: iron-man/README.md (player instructions)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-editor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-editing-space]: KSA.VehicleEditingSpace (decompiled, build 2026.10.10.5554)
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^ksa-kitten-eva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-orbit-controller]: KSA.OrbitController (decompiled, build 2026.10.10.5554)
[^scope-iron-man]: scope/iron-man.md (authors' hook inventory, build 5482)
[^iron-man-research]: plans/iron-man/RESEARCH.md (authors' design research, build 5402)
