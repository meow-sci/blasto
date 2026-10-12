---
type: How-To
title: Submit StaticMeshRenderables from a ClearBuckets postfix for every viewport
description: Draw a lit, shadowed glTF mesh with the engine's own StaticMeshRenderable by submitting it from a postfix on SuperMeshRenderSystem.ClearBuckets, once per rendered viewport
category: rendering
group: meshes
topic: draw-mod-meshes-with-the-engine-renderers
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/mod-mesh-draw-hook]
techniques: [harmony-postfix, glb-model, runtime-mesh-draw, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.SuperMeshRenderSystem.ClearBuckets]
lifecycle: [startup:all-mods-loaded, frame:render, render:scene-opaque, render:shadows, render:translucency]
game_types: [KSA.SuperMeshRenderSystem, KSA.StaticMeshRenderable, KSA.Program, KSA.IViewport, KSA.Camera]
tags: [mesh, static-mesh, buckets, viewport, ego-frame, gltf]
---
Outcome: a glTF mesh of your own (a ball, a prop, a course piece) drawn in the world with the game's PBR shader, ambient occlusion and shadows, in the main view and in every secondary camera window. No pipeline, no Vulkan code: you build a `StaticMeshRenderable`, set its `Transform` and call `Draw` at the one moment the engine keeps the result.

Demonstrated by KSAGolf ([Drawing a mod's static mesh into every viewport](../../../../mods/ksagolf/features/mod-mesh-draw-hook.md)). Prerequisites: StarMap, Harmony, a `<GltfFile>` and a `<PbrMaterial>` asset declared in your mod's XML.

## Background: how the game does it

`StaticMeshRenderable` [current/decomp/KSA/StaticMeshRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticMeshRenderable.cs#L9-L90) registers each mesh of a glTF asset with the SuperMesh pass buckets in its constructor. `Draw(ViewHandle)` does not draw anything itself: it appends one `InstanceData { model = Transform }` per mesh to the buckets of that view, and, when `CastShadows` is true, one `ShadowRenderable` per mesh to `SuperMeshRenderSystem.ShadowRenderablePool`. The buckets are consumed later by `RenderMainPass`, the prepass and the translucency pass.

The renderer starts each viewport's frame by emptying those buckets:

```csharp
// current/decomp/KSA/SuperMeshRenderSystem.cs L546-L554
public void ClearBuckets(IViewport viewport)
{
	ViewHandle view = ViewForViewport(viewport);
	PassBucketSystem.Clear(view, MeshPass.OpaquePrePass);
	PassBucketSystem.Clear(view, MeshPass.TranslucentPrePass);
	PassBucketSystem.Clear(view, MeshPass.MainOpaque);
	PassBucketSystem.Clear(view, MeshPass.Translucency);
	ShadowRenderablePool.Clear();
}
```

`Program.RenderViewport` (secondary views) and `RenderGame` (main view) call `ClearBuckets` right after `SetViewOptions`, then submit kittens, parachutes and seated crew into the freshly cleared view ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4481-L4495), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4688-L4701)). `RenderEditor` calls it too ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4984)). Anything you submit earlier in the frame, for example from `StarMapAfterGui` or a `Universe.UpdateRenderData` hook, is cleared before a single command is recorded. There is no error: the mesh simply never appears.

## Lifecycle and timing

| Step | Where it runs | Lifecycle value |
|---|---|---|
| Install the Harmony postfix | `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Postfix runs, you call `Draw` | Inside `Program.Render` then `RenderGame` / `RenderViewport` / `RenderEditor`, once per rendered viewport, secondary views first and the main view last | `frame:render` |
| Instances are consumed | prepass and `SuperMeshRenderSystem.RenderMainPass`; the translucency pass for see-through meshes; the cascaded shadow pass of the main view | `render:prepass`, `render:scene-opaque`, `render:translucency`, `render:shadows` |

See the RenderGame pass table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence): rows 1 and 1a (viewport setup and `ClearBuckets`), 2 (prepass), 6 (cascaded shadows, which call `AddPoolToShadowBucket`), 9 (`RenderMainPass`) and 14 (`RenderTranslucencyPass`).

- The postfix runs on the main thread while a command buffer is being recorded. Submit and read state; do not create pipelines or call `WaitIdle`.
- By then every camera has been posed (`OnFrameViewports`), so `viewport.GetCamera().PositionEcl` is exactly the camera this view is drawn from.
- The shadow pool is cleared by every viewport's `ClearBuckets`, and the main view clears it last, so only meshes submitted into the main view cast cascaded shadows.

## Steps

1. **Declare the assets.** A `<GltfFile>` with one mesh and a `<PbrMaterial>` with diffuse, normal and AO/roughness/metal maps, listed in `mod.toml` `assets`:

   ```xml
<!-- src/KSAGolf/KSAGolfProps.xml L14-L21 -->
<GltfFile Id="KSAGolf_Putter_Glb">
    <Source Path="Meshes/KSAGolf_Putter.glb"/>
</GltfFile>
<PbrMaterial Id="KSAGolf_Putter_Material">
    <Diffuse Path="Textures/KSAGolf_Putter_Diffuse.png" Category="Default"/>
    <Normal Path="Textures/KSAGolf_Putter_Normal.png" Category="Default"/>
    <AoRoughMetal Path="Textures/KSAGolf_Putter_PBR.png" Category="Default"/>
</PbrMaterial>
```

2. **Build the renderable once.** KSAGolf's `AttachmentMesh.TryBuild` loads the glTF and material through the SuperMesh asset managers, writes the material into every slot, and constructs the renderable with the static PBR technique and the static prepass technique. It uses reflection because `AssetName` lives in `Planet.Core.dll`, which the mod does not reference; if you do reference it, `new StaticMeshRenderable(system.MeshRendererStaticPbr, new AssetName(id), system.MeshRendererStaticPrePass)` () is the same call.

   ```csharp
// src/KSAGolf/Ksa/AttachmentMesh.cs L26-L69
public static bool TryBuild(string gltfId, string materialId, out StaticMeshRenderable? mesh, out string why, bool seeThrough = false)
{
    mesh = null;
    why = string.Empty;
    try
    {
        if (AssetNameType is null)
        {
            why = "LoadedAssetRef.Id not found";
            return false;
        }

        SuperMeshRenderSystem system = Program.Instance.SuperMeshRenderSystem;
        object gltfName = Activator.CreateInstance(AssetNameType, gltfId)!;
        object materialName = Activator.CreateInstance(AssetNameType, materialId)!;

        if (Load(system, "GltfSystem", gltfName) is not GltfPbrAssetRef gltf)
        {
            why = $"glTF '{gltfId}' did not load";
            return false;
        }

        if (Load(system, "MaterialSystem", materialName) is not GpuObjectAssetRef material)
        {
            why = $"material '{materialId}' did not load";
            return false;
        }

        // An exported .glb names a material slot, which is what gives this array a length.
        for (int i = 0; i < gltf.Materials.Length; i++) gltf.Materials[i] = material;

        object technique = seeThrough ? Program.Instance.CharacterRenderResources.GlassRenderer : system.MeshRendererStaticPbr;
        mesh = (StaticMeshRenderable?)Activator.CreateInstance(
            typeof(StaticMeshRenderable), technique, gltfName, system.MeshRendererStaticPrePass, !seeThrough);
        if (mesh is null) why = "the renderable could not be built";
        else if (seeThrough) mesh.CastShadows = false;
        return mesh is not null;
    }
    catch (Exception e)
    {
        why = $"building '{gltfId}' threw: {e.GetBaseException().Message}";
        return false;
    }
}
```

   `seeThrough: true` switches to the character glass technique (`CharacterRenderResources.GlassRenderer`) and `isOpaque: false`, which lands the mesh in the translucent pass; such a mesh should not cast a shadow.

3. **Install the postfix by exact signature**, and refuse to throw from it. Resolve `ClearBuckets(IViewport)` with `GetMethod` so a changed parameter list logs once instead of crashing:

   ```csharp
// src/KSAGolf/Ksa/ViewDrawHook.cs L30-L55
public static void Install(Action<IViewport> draw)
{
    _draw = draw;
    if (Installed) return;

    try
    {
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
        Log.Info("meshes of the mod's own are drawn, via SuperMeshRenderSystem.ClearBuckets");
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the viewport render, so the ball is not drawn: {e.Message}");
    }
}
```

4. **The postfix itself**, with the parameter name `viewport` matching the original as Harmony requires. One fault disables the draw for the session rather than the frame:

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

   It is installed from the mod entry point:

   ```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L39-L52
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    // Steps a frame the GUI pass skipped before the render rather than after it. A refusal
    // degrades to the frame postfix, so it costs a frame and nothing else.
    PreRenderHook.Install(StepOnce);

    // The only way to see a save load: StarMap has no hook for one, and the mod never leaves the
    // flight scene across it.
    WorldReloadHook.Install();

    _golf = new Golf(_config);
    _builder = new CourseBuilder(_golf);
    ViewDrawHook.Install(DrawIntoView);
```

5. **Place and draw per viewport.** `Transform` is in the camera-relative (ego) frame of the view being drawn, in metres, so compute it from the viewport you were handed, not from the main camera. The ball is authored in centimetres (scale 0.01), spun, then translated:

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

6. **Per-view differences.** A mesh meant for one camera only checks the viewport. The first-person putter is built lazily and drawn into the main view only:

   ```csharp
// src/KSAGolf/Ksa/Golf.cs L457-L467
private void DrawViewClub(IViewport viewport)
{
    if (!ReferenceEquals(viewport, Program.MainViewport) || !_config.FirstPerson || !_borrowed || _phase != Phase.Addressing) return;
    if (_kitten is not { } kitten || !KsaWorld.IsAlive(kitten)) return;

    _viewClub ??= AttachmentMesh.TryBuild("KSAGolf_Putter_Glb", "KSAGolf_Putter_Material", out StaticMeshRenderable? built, out _) ? built : null;
    if (_viewClub is null) return;

    _viewClub.Transform = GolfPose.ClubInModel(_rig, Posture(), _stroke.Theta) * KittenFrame.ModelToEgo(kitten, viewport.GetCamera().PositionEcl);
    _viewClub.Draw(Program.Instance.SuperMeshRenderSystem.ViewForViewport(viewport));
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/ViewDrawHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/ViewDrawHook.cs) | Postfix install, removal, fault handling |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L216-L220) | `DrawIntoView` delegate fanning out to `Golf` and `CourseBuilder` |
| [src/KSAGolf/Ksa/AttachmentMesh.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/AttachmentMesh.cs) | Builds `StaticMeshRenderable` from declared assets |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L441-L467) | Ball and club draws |
| [src/KSAGolf/KSAGolfProps.xml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolfProps.xml) | Asset declarations |

## Variations and gotchas

- **Silent failure.** A draw submitted at the wrong time throws nothing and logs nothing. If a mesh is missing, check you are inside the `ClearBuckets` postfix, not before it.
- **Editor.** The postfix also fires in `RenderEditor`. Filter by `Program.Editor` or viewport if your mesh belongs to flight only.
- **Construct once, outside the loop if possible.** The constructor registers buckets with the pass system, which KSAGolf does lazily on first draw inside the postfix; that works but means construction happens mid-recording. Build in `StarMapAllModsLoaded` when the assets are already loaded.
- **Dispose.** `StaticMeshRenderable.Dispose` unregisters the bucket handles; call it when you discard a mesh.
- **Range cull yourself.** KSAGolf skips the ball beyond 5 km; the engine does not cull `StaticMeshRenderable` instances for you.
- **Shadow culling.** In build 2026.10.10.5554 `Draw` fills `ShadowRenderable.Center` from `Transform.Translation`, which cascade culling uses, so no extra work is needed.
- **Pinning the signature.** `nameof(SuperMeshRenderSystem.ClearBuckets)` compiles to a string; a call such as KSArmory's `PinTheSignature` (see approach 3) makes a rename a build error.

## See also

- [Overview](overview.md)
- [Approach 2: private buffers in the static-object pipeline](draw-mod-meshes-with-the-engine-renderers-2.md), [approach 3: extra part-model instances](draw-mod-meshes-with-the-engine-renderers-3.md), [approach 4: clone interleaved part meshes](draw-mod-meshes-with-the-engine-renderers-4.md)
- [Drawing a mod's static mesh into every viewport](../../../../mods/ksagolf/features/mod-mesh-draw-hook.md)
- [Make static objects cast sun and lamp shadows with invisible casters](../add-invisible-shadow-casters/overview.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
