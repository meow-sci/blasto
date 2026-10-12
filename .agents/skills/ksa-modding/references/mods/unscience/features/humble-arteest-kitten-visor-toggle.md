---
type: Mod Feature
title: Hide kitten helmet visors with a draw-call transpiler
description: A Harmony transpiler on KittenRenderable.UpdateRenderData replaces exactly one VisorMesh.Draw(ViewHandle) call with a static helper that skips the draw while a session flag is set, hiding visor glass on every kitten in every pass and view without touching materials or meshes
mod: unscience
submod: humble-arteest
category: kittens
techniques: [harmony-transpiler]
hooks:
  - harmony-transpiler KSA.KittenRenderable.UpdateRenderData
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Hide/Show button)
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types:
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.StaticMeshRenderable
  - RenderCore.Systems.ViewHandle
  - KSA.KittenEva
  - KSA.IVASeat
  - KSA.Vehicle
  - KSA.Program
tags: [kittens, helmet, visor, transpiler, il, rendering]
---

The kitten helmet visor is a separate translucent mesh whose shader ignores material alpha, so it
cannot be faded away by recolouring. Kitten Color's **Hide visor glass** button removes it from every
kitten, including kittens that spawn later, and **Show** brings it back. Helmet shells, eyes and
vehicle windows are untouched.[^kitten-color-submod]

Mechanism in one line: a transpiler finds the single `ldfld CharacterAvatar.Helmet::VisorMesh`,
`ldloc view`, `callvirt StaticMeshRenderable::Draw(ViewHandle)` sequence inside
`KittenRenderable.UpdateRenderData` and turns the call into `call KittenVisorPatches.DrawVisor(StaticMeshRenderable, ViewHandle)`,
which only forwards to `Draw` while `Hidden` is false.[^visor-patches]

## How it works

1. `Apply` resolves `KittenRenderable.UpdateRenderData` by name (single overload) and patches it with
   the transpiler; any exception leaves `IsApplied = false`, forces `Hidden = false` and stores an
   error that the UI shows next to a disabled button.[^visor-patches]
2. The transpiler resolves the `VisorMesh` field on the nested `CharacterAvatar.Helmet` struct and the
   exact `StaticMeshRenderable.Draw(ViewHandle)` overload, then scans for the three-instruction window
   (`ldfld VisorMesh`, any `ldloc`, a call to `Draw`). Matching the receiver matters: the helmet shell
   is drawn with the same method one line earlier. Anything other than exactly one match throws, so
   a game change disables the feature instead of hiding the wrong mesh.
3. The replacement keeps the stack contract (consume the mesh reference and the view, return void)
   and the instruction object (so labels and exception blocks stay valid); it only swaps opcode and
   operand.
4. `DrawVisor` checks the static `Hidden` flag. Skipping `Draw(view)` skips every bucket the visor
   would have entered for that view: the translucent colour pass and the opaque depth prepass (the
   visor is built with `isOpaque: false` and `CastShadows = false`).[^ksa-characteravatar][^ksa-staticmeshrenderable]

Why a draw gate: the visor uses `ModelTranslucent.frag`
([current/Content/Core/Shaders/Mesh/ModelTranslucent.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/ModelTranslucent.frag#L68-L81)), whose non-eye branch hard-codes
`opacity = 0.75` and mixes toward 1 with Fresnel, so `AlbedoColor.w` has no effect.[^ksa-modeltranslucent-frag]

## Lifecycle and timing

| Step | Where (build 5554) | Lifecycle |
|---|---|---|
| Transpiler applied | `[StarMapAllModsLoaded]` -> unscience `Patcher.Patch()` -> `TryApply("kitten-visor", ...)` | `startup:all-mods-loaded` |
| Toggle | Kitten Color panel button (`[StarMapAfterGui]`); deactivating the panel, dispose, save reset and patch removal force `Hidden = false` | `frame:draw-ui` |
| Patched draw runs | Inside `Program.RenderGame`, before `PrePassRenderer.Render` (L4713): EVA kittens via `vehicle.UpdateRenderData(RenderedViewport, ...)` for `KittenEva` (L4698) -> `KittenEva.UpdateRenderData` -> `KittenRenderable.UpdateRenderData`; seated crew via `ControlledVehicle?.UpdateSeatedCrewRenderData` (L4702) -> `IVASeat.UpdateSeatedRenderable`. Secondary and portrait viewports do the same in `RenderViewport` (L4489-L4495) | `frame:render` (mesh-bucket submission at the start of `RenderGame` command recording, consumed later by the prepass, shadow and main passes) |

Unlike part models, kittens are not updated in `UpdateRenderingResources`; their render data is built
per viewport at the start of command recording, and `Draw(view)` only enqueues instances into that
view's mesh buckets, which the prepass and colour passes consume later in `RenderGame`. A toggle set in
the GUI phase therefore applies to the same frame, in every view.[^ksa-program][^ksa-kitteneva][^ksa-ivaseat][^ksa-vehicle]

## Key code

| Path | Role |
|---|---|
| [humble-arteest.lib/KittenVisorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenVisorPatches.cs) | Apply/Remove, transpiler, `DrawVisor` |
| [humble-arteest.lib/KittenColorSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColorSubmod.cs#L57-L101) | Button, reset of `Hidden` on deactivate and dispose |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L84-L86) | Applied with the other humble-arteest patches |

## Snippets

The receiver-anchored call swap:

```csharp
// humble-arteest.lib/KittenVisorPatches.cs L53-L88
    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        var visorField = AccessTools.Field(typeof(CharacterAvatar.Helmet), nameof(CharacterAvatar.Helmet.VisorMesh));
        // KSA 5482 draws per render view: VisorMesh.Draw(view), with the view loaded between.
        var draw = AccessTools.Method(typeof(StaticMeshRenderable), nameof(StaticMeshRenderable.Draw),
            new[] { typeof(ViewHandle) });
        var replacement = AccessTools.Method(typeof(KittenVisorPatches), nameof(DrawVisor));
        int matchIndex = -1;
        int matches = 0;

        // Match the receiver, not just Draw(view): the adjacent helmet draw must stay stock.
        for (int i = 2; i < code.Count; i++)
        {
            if (code[i - 2].opcode == OpCodes.Ldfld && Equals(code[i - 2].operand, visorField)
                && code[i - 1].IsLdloc() && code[i].Calls(draw))
            {
                matchIndex = i;
                matches++;
            }
        }

        if (matches != 1)
            throw new InvalidOperationException($"Expected one VisorMesh.Draw call, found {matches}.");

        // Same stack effect: consume the mesh reference and view, return void. Preserve labels/blocks.
        code[matchIndex].opcode = OpCodes.Call;
        code[matchIndex].operand = replacement;
        return code;
    }

    private static void DrawVisor(StaticMeshRenderable visor, ViewHandle view)
    {
        if (!Hidden)
            visor.Draw(view);
    }
```

The game code being rewritten:

```csharp
// current/decomp/KSA/KittenRenderable.cs L356-L371
		ViewHandle view = Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport);
		_characterAvatar.Core.CharacterModel.HideMaskedMeshes = HideHead;
		_characterAvatar.Core.CharacterModel.Draw(view);
		if (!HideHead)
		{
			_characterAvatar.Fur.CatFurRenderable.Draw(view);
		}
		if (_characterAvatar.Attachments.Helmet.HasAttachment)
		{
			float4x4 boneTransform = _characterAvatar.Core.CharacterModel.GetBoneTransform(_characterAvatar.Attachments.Helmet.SocketIndex);
			float4x4 float4x6 = float4x4.CreateRotationZ(-(float)Math.PI / 2f) * float4x4.CreateRotationX(-(float)Math.PI / 2f);
			_characterAvatar.Attachments.Helmet.HelmetMesh.Transform = _characterAvatar.Attachments.Helmet.HelmetTransform * (float4x6 * boneTransform);
			_characterAvatar.Attachments.Helmet.VisorMesh.Transform = _characterAvatar.Attachments.Helmet.VisorTransform * (float4x6 * boneTransform);
			_characterAvatar.Attachments.Helmet.HelmetMesh.Draw(view);
			_characterAvatar.Attachments.Helmet.VisorMesh.Draw(view);
		}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `KittenRenderable.UpdateRenderData(IViewport, int, double, float4x4, double3, double3, in LocomotionState, in KittenAnimInputs)` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L311-L371) | Patched (transpiler); visor draw at L370 |
| `CharacterAvatar.Helmet.VisorMesh` | [current/decomp/KSA/CharacterAvatar.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CharacterAvatar.cs#L103-L117) | Field matched in IL; mesh built `isOpaque: false`, `CastShadows = false` at L467 |
| `StaticMeshRenderable.Draw(ViewHandle)`, `Visible` | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L29-L60) | Per-view bucket submission |
| `KittenEva.UpdateRenderData(IViewport, int)` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | EVA caller |
| `IVASeat.UpdateSeatedRenderable` | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L104) | Seated-crew caller |
| `Vehicle.UpdateSeatedCrewRenderData` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3728-L3741) | Iterates the controlled vehicle's crew |
| `Program.RenderGame` / `RenderViewport` kitten updates | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4689-L4713) | Per-viewport call sites before the prepass |

## Pitfalls and version notes

- **5482 signature change.** Rev 5474 bucketed meshes per view: `Draw()` became `Draw(ViewHandle)`
  and a local `view` is loaded between the field load and the call, so the old "adjacent `ldfld` +
  `Draw()`" pattern stopped matching. The transpiler was rewritten for the three-instruction window;
  unchanged in 5554.[^scope-materials]
- **Simpler alternative.** `StaticMeshRenderable` has a public `Visible` property that `Draw` checks,
  so setting `VisorMesh.Visible = false` on each kitten's avatar would also work, but requires walking
  every kitten (EVA and seated) and catching new spawns; the transpiler gates all of them with one
  static flag and no reflection on avatars.[^ksa-staticmeshrenderable]
- The flag is global and session-only in the standalone host; in unscience it is saved with the
  humble-arteest participant (`HideVisor`).
- Native visual acceptance (multiple kittens, spawn while hidden, view changes) was still pending at
  the pinned ref.[^scope-materials]

## See also

- [Kitten colour](./humble-arteest-kitten-color.md)
- [Iron Man equipment render ordering](./iron-man-equipment-render-ordering.md) (another kitten draw-order patch)
- [Humble Arteest sub-mod](../submods/humble-arteest.md) and the [mod overview](../overview.md)
- [Transpile the single VisorMesh.Draw call into a gated forwarder](../../../howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-4.md) - how-to article demonstrating this feature
- [Know where and when a kitten is animated and drawn, per viewport](../../../howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md) - how-to article demonstrating this feature
- [Gate helper that skips the call for the mod's own condition](../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-4.md) - how-to article demonstrating this feature

[^visor-patches]: humble-arteest.lib/KittenVisorPatches.cs
[^kitten-color-submod]: humble-arteest.lib/KittenColorSubmod.cs
[^scope-materials]: scope/character-and-materials.md (authors' integration analysis)
[^ksa-characteravatar]: KSA.CharacterAvatar (decompiled, build 2026.10.10.5554)
[^ksa-staticmeshrenderable]: KSA.StaticMeshRenderable (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-ivaseat]: KSA.IVASeat (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-modeltranslucent-frag]: Mesh/ModelTranslucent.frag (game shader, build 2026.10.10.5554)
