---
type: Mod Feature
title: Drawing a mod's static mesh into every viewport
description: Postfix SuperMeshRenderSystem.ClearBuckets so a mod's own StaticMeshRenderable (the ball, course pieces, a see-through ghost) is submitted after the engine clears each viewport's mesh buckets, the one moment such a draw survives to the render
mod: ksagolf
category: rendering
techniques: [harmony-postfix, glb-model, runtime-mesh-draw, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets]
lifecycle: [startup:all-mods-loaded, frame:render, render:scene-opaque, render:shadows, render:translucency]
game_types:
  - KSA.SuperMeshRenderSystem
  - KSA.StaticMeshRenderable
  - KSA.Program
  - KSA.IViewport
  - KSA.Camera
  - KSA.Celestial
  - KSA.ShadowRenderable
  - KSA.Universe
tags: [rendering, mesh, static-mesh, buckets, viewport, harmony, postfix, gltf, ego-frame]
---

KSAGolf draws its golf ball, its course pieces and the course builder's see-through "ghost" with
the engine's own `StaticMeshRenderable`. These are lit, shadowed and depth-tested like any part.
Calling `StaticMeshRenderable.Draw(view)` from a StarMap hook does nothing visible: the draw is
recorded into the view's mesh buckets, and the renderer clears those buckets for each viewport just
before it fills them for real.[^ksa-program][^ksa-supermesh] The mod's notes put it as "a static
mesh of a mod's own is drawn after `ClearBuckets`, or not at all".[^golf-modnotes]

`Ksa/ViewDrawHook.cs` therefore postfixes the public
`SuperMeshRenderSystem.ClearBuckets(IViewport)` and calls one delegate per rendered viewport. The
delegate (`KSAGolfMod.DrawIntoView`) hands the viewport to `Golf.Draw` and `CourseBuilder.Draw`.

## How it works

1. **Where the clear happens.** For each visible secondary viewport, `Program.RenderGame` calls
   `RenderViewport`, which runs `SetViewOptions` then `ClearBuckets(RenderedViewport)` and then draws
   the EVA kittens into that view (`KittenEva.UpdateRenderData`; kittens are left out of
   `UpdateRenderingResources`). It then does the same inline for the main viewport. `RenderEditor`
   calls it too.[^ksa-program]
2. **What the clear removes.** `ClearBuckets` empties the `OpaquePrePass`, `TranslucentPrePass`,
   `MainOpaque` and `Translucency` buckets for that view's `ViewHandle`, and clears the shared
   `ShadowRenderablePool`.[^ksa-supermesh]
3. **The postfix.** It runs right after the clear and before the kittens are drawn into the view. It
   invokes the delegate inside a `try`. On the first exception it nulls the delegate and logs once,
   so a broken draw costs the ball for the session and never the frame.[^golf-viewdrawhook]
4. **Placing the mesh.** `StaticMeshRenderable.Transform` is in the camera-relative (ego) frame of
   the view being drawn, in metres: `positionEcl − viewport.GetCamera().PositionEcl`. The ball lives
   in the body-fixed frame of its planet. Its draw matrix is a 0.01 scale (the mesh is authored in
   centimetres for use as a kitten attachment), then its spin composed with
   `Celestial.GetCcf2Cce()`, then the ego translation. It is skipped beyond 5 km.[^golf-golf][^ksa-celestial]
5. **Submitting.** `mesh.Draw(Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport))`
   writes into that view's buckets and, if `CastShadows` is set, into the shadow
   pool.[^ksa-staticmesh]
6. **Which meshes.**
   - The ball, in every viewport.
   - In first person at address, a second putter built only for the main view. It is placed with
     `KittenFrame.ModelToEgo` against that viewport's camera, because the held one is folded away
     with the body.
   - Course pieces, in every viewport.
   - The ghost piece, built `seeThrough: true` on the visor's glass technique so it lands in the
     translucent pass.

   All are built with `AttachmentMesh.TryBuild` (see
   [Kitten held props](../features/kitten-held-props.md)).[^golf-coursebuilder][^golf-attachmentmesh]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| Install the Harmony postfix | `[StarMapAllModsLoaded] OnFullyLoaded`, after the props XML is loaded; removed when the fault limit is reached (the `[StarMapUnload]` removal runs at process exit, after StarMap has already unpatched everything; see [Degrading Harmony patches](degrading-harmony-patches.md))[^golf-mod] | `startup:all-mods-loaded` |
| Postfix runs, delegate draws | Inside `Program.Render` → `RenderGame` (or `RenderEditor`), once per rendered viewport, secondary views first and the main view last, before `PrePassRenderer.Render`[^ksa-program] | `frame:render` |
| The submitted instances are consumed | Prepass and main opaque pass of that view; the main view's cascaded shadow pass (`AddPoolToShadowBucket`); the translucent pass for the ghost | `render:scene-opaque`, `render:shadows`, `render:translucency` |

**Why here, and what is safe.**

- **Earlier is too early.** `OnPreRender` and `UpdateRenderingResources` (where
  `Universe.UpdateRenderData` runs) both happen before `RenderGame`. A draw from either, or from any
  StarMap hook, is wiped by the clear.[^ksa-program]
- **State is settled.** By the time the postfix runs, the step has already moved the ball for this
  frame (in the GUI pass) and every camera was posed in `OnFrameViewports`. Reading
  `viewport.GetCamera().PositionEcl` here gives exactly the camera the view is drawn from. A
  position converted to ego against a different camera would be wrong for this one.
- **Allowed work.** The postfix runs on the main thread while a Vulkan command buffer is being
  recorded. Submitting draws and reading state is what it is for. The ball's renderable is built
  when the ball is dropped. The first-person club and the course-piece renderables, however, are
  built lazily on their first draw, inside this postfix. So the mod relies on constructing a
  `StaticMeshRenderable`, which registers mesh buckets, in the middle of recording.
- **Shadow pool timing.** The shadow pool is cleared by every view's `ClearBuckets`, and the
  cascaded shadow pass is recorded for the main view after its own clear. So only draws made into
  the main view cast shadows.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/ViewDrawHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/ViewDrawHook.cs) | Installs the postfix by exact signature, swallows and reports faults |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L216-L220) | `DrawIntoView`: the delegate the hook calls |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L441-L467) | Ball and first-person club draws |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L535-L640) | Course pieces into every viewport; ghost through the glass technique; gizmo lines on the main view only |
| [src/KSAGolf/Ksa/AttachmentMesh.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/AttachmentMesh.cs) | Builds each `StaticMeshRenderable` from a declared glTF and material |

## Snippets

```csharp
// src/KSAGolf/Ksa/ViewDrawHook.cs L37-L48
            MethodInfo? target = typeof(SuperMeshRenderSystem).GetMethod(nameof(SuperMeshRenderSystem.ClearBuckets),
                                                                         BindingFlags.Public | BindingFlags.Instance, [typeof(IViewport)]);
            if (target is null)
            {
                Log.Warn("the ball cannot be drawn: KSA has no SuperMeshRenderSystem.ClearBuckets(IViewport)");
                return;
            }

            MethodInfo postfix = typeof(ViewDrawHook).GetMethod(nameof(AfterClearBuckets), BindingFlags.NonPublic | BindingFlags.Static)!;
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Installed = true;
```

```csharp
// src/KSAGolf/Ksa/ViewDrawHook.cs L74-L88
    private static void AfterClearBuckets(IViewport viewport)
    {
        try
        {
            _draw?.Invoke(viewport);
        }
        catch (Exception e)
        {
            _draw = null;

            if (_complained) return;
            _complained = true;
            Log.Error("drawing into the world failed; the ball is no longer drawn this session", e);
        }
    }
```

```csharp
// src/KSAGolf/Ksa/Golf.cs L441-L455
    /// <summary>Draws the ball into one viewport, as its viewport is rendered, and in first person the club.</summary>
    public void Draw(IViewport viewport)
    {
        DrawViewClub(viewport);
        if (_ball is null || _ballBody is null || _ballMesh is null) return;

        double3 ego = BallEcl() - viewport.GetCamera().PositionEcl;
        if (!Vec.IsFinite(ego) || Vec.Len(ego) > 5000.0) return;

        doubleQuat turned = doubleQuat.Concatenate(_ball.Spin, _ballBody.GetCcf2Cce());
        _ballMesh.Transform = float4x4.CreateScale(0.01f)
                              * float4x4.CreateFromQuaternion(floatQuat.Pack(turned))
                              * float4x4.CreateTranslation(float3.Pack(ego));
        _ballMesh.Draw(Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport));
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `SuperMeshRenderSystem.ClearBuckets(IViewport)` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L546-L554) | The patched method: clears the view's four mesh passes and the shadow pool |
| `SuperMeshRenderSystem.ViewForViewport` / `ShadowRenderablePool` / `AddPoolToShadowBucket` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L237-L240) | The view handle to draw into; how the main view's shadows read the pool |
| `Program.RenderViewport` / `RenderGame` / `RenderEditor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4468-L4495) | Callers of `ClearBuckets`, then kittens drawn, then passes recorded (`RenderGame` at L4646-L4700) |
| `Program.UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4436) | Runs `Universe.UpdateRenderData` before the clear, which is why a draw from there is lost |
| `StaticMeshRenderable.Transform` / `Draw(ViewHandle)` / `CastShadows` | [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L56-L90) | One instance per mesh into all passes, plus a shadow entry |
| `ShadowRenderable` | [current/decomp/KSA/ShadowRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShadowRenderable.cs) | Shadow pool entry, now with an explicit `Center` |
| `IViewport.GetCamera().PositionEcl` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L490) | The camera the view is drawn from (`Program.RenderedViewport` is the view being rendered) |
| `Celestial.GetCcf2Cce` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L532-L535) | Ball from body-fixed to ecliptic, and its orientation |

## Pitfalls and version notes

- **Silent failure.** A draw made at the wrong time counts as made: nothing throws and nothing logs.
  The only symptom is that the mesh is missing.[^golf-modnotes]
- **Every viewport.** The postfix runs for secondary viewports and in the vehicle editor too. The
  delegate decides per view. `Golf` draws its first-person club only into `Program.MainViewport`,
  and the course builder draws gizmo lines only there.
- **Ego is per camera.** Compute the ego translation from the viewport passed in. A position taken
  against the main camera and drawn into a secondary view is off by the distance between the two
  cameras.
- **Half-pinned signature.** The target is found with `nameof(SuperMeshRenderSystem.ClearBuckets)`,
  so a rename is a compile error. But `nameof` compiles to a string and emits no member reference,
  so `ClearBuckets` does not appear in the generated `docs/KSA-API-SURFACE.md` (CLAUDE.md describes it
  as pinned there) and the update tooling will not flag a change in its behaviour. A changed
  parameter list makes `GetMethod` return null at runtime; the mod then logs one line and the ball is
  not drawn; nothing else depends on it.[^golf-claude] See
  [API surface record and assembly lock](../features/api-surface-and-assembly-lock.md).
- **Version notes.** Built against 2026.9.22.5482 (where `RenderViewport` called `ClearBuckets` at
  `Program.cs:4419`). In 2026.10.10.5554:
  - the clear is at L4482 (secondary views) and L4689 (main view), with the same order;
  - `StaticMeshRenderable.Draw` now fills `ShadowRenderable.Center`, which cascade culling uses, so
    mod meshes drawn through it are culled correctly with no change on the mod's side;
  - secondary viewports now carry `RenderTerrain`, which changes nothing here.[^ksa-shadowrenderable][^ksa-staticmesh]

## See also

- [Mod meshes held by a kitten as cosmetic attachments](../features/kitten-held-props.md)
- [Putting session with a mouse-swung club](../features/putting-session.md)
- [Golf ball physics in the body-fixed frame](../features/golf-ball-physics.md)
- [Top-down minigolf course builder](../features/minigolf-course-builder.md)
- [Frame and epoch draw anchor](../features/frame-epoch-draw-anchor.md)
- [API surface record and assembly lock](../features/api-surface-and-assembly-lock.md)
- [Prop and course asset XML](../features/prop-and-course-asset-xml.md)
- [Course frame anchored at the kitten and saved as latitude, longitude, heading and floor height](../../../howto/celestials/terrain/place-static-objects-on-a-planet-surface/place-static-objects-on-a-planet-surface-3.md) - how-to article demonstrating this feature
- [Submit StaticMeshRenderables from a ClearBuckets postfix for every viewport](../../../howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-1.md) - how-to article demonstrating this feature
- [Body-fixed mesh with Ccf-to-Cce rotation and body position at draw time](../../../howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-5.md) - how-to article demonstrating this feature

[^golf-viewdrawhook]: src/KSAGolf/Ksa/ViewDrawHook.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-coursebuilder]: src/KSAGolf/Ksa/CourseBuilder.cs
[^golf-attachmentmesh]: src/KSAGolf/Ksa/AttachmentMesh.cs
[^golf-modnotes]: docs/KSA-MODDING-NOTES.md
[^golf-claude]: CLAUDE.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-supermesh]: KSA.SuperMeshRenderSystem (decompiled, build 2026.10.10.5554)
[^ksa-staticmesh]: KSA.StaticMeshRenderable (decompiled, build 2026.10.10.5554)
[^ksa-shadowrenderable]: KSA.ShadowRenderable (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
