---
type: How-To
title: Transpile the single VisorMesh.Draw call into a gated forwarder
description: Match ldfld VisorMesh, ldloc view, call StaticMeshRenderable.Draw inside KittenRenderable.UpdateRenderData and redirect it to a forwarder that skips the draw while a flag is set
category: kittens
group: appearance
topic: tint-and-restyle-kitten-appearance
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/humble-arteest-kitten-visor-toggle]
techniques: [harmony-transpiler]
hooks: [harmony-transpiler KSA.KittenRenderable.UpdateRenderData, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types: [KSA.KittenRenderable, KSA.CharacterAvatar, KSA.StaticMeshRenderable, KSA.KittenEva, KSA.IVASeat]
tags: [kitten, visor, transpiler, humble-arteest]
---
The helmet visor is a separate translucent mesh drawn by a shader that ignores material alpha, so it cannot be faded out by recolouring (see [approach 1](tint-and-restyle-kitten-appearance-1.md)). To hide it you must stop it being drawn. This article does that globally with a Harmony transpiler that rewrites the single `VisorMesh.Draw(view)` call inside `KittenRenderable.UpdateRenderData` into a call to a gated forwarder. The helmet shell, eyes and vehicle windows stay stock, and seated crew are covered too. Prerequisites: StarMap and Harmony ([transpilers](../../../patching/transpilers/index.md)). Demonstrated by Unscience's Kitten Color panel: [Kitten visor toggle](../../../../mods/unscience/features/humble-arteest-kitten-visor-toggle.md). For a per-avatar alternative that needs no patch, see [approach 5](tint-and-restyle-kitten-appearance-5.md).

## Background: how the game does it

Every view that draws a kitten calls `KittenRenderable.UpdateRenderData`, which draws the body, fur, then the helmet shell and visor with `Draw(view)`; `Draw` only enqueues instances into that view's mesh buckets (colour pass and depth prepass):

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

The visor is built non-opaque with no shadow, and the glass shader hard-codes opacity:

```csharp
// current/decomp/KSA/CharacterAvatar.cs L467-L470
			helmet.VisorMesh = new StaticMeshRenderable(glassRenderer, gltf2.Id, meshRendererStaticPrePass, isOpaque: false)
			{
				CastShadows = false
			};
```

```glsl
// current/Content/Core/Shaders/Mesh/ModelTranslucent.frag L72-L81
    // TODO TEMP - HARD CODED
    #ifdef EYE
    float opacity = 0.3;//albedo.a;
    roughness = 0.33;
    metallic = 0.2;
    ao = 1;
    vec3 color = vec3(0.15,0.15,0.15);
    #else
    float opacity = 0.75;
    vec3 color = mix(albedo.xyz,vec3(0.1,0.1,0.1), 0.9);
```

`StaticMeshRenderable` also exposes a public `Visible` property that `Draw` checks; setting `VisorMesh.Visible = false` on every avatar would also work, but then you must walk every kitten (EVA and seated) and catch new spawns. The transpiler gates all of them with one static flag:

```csharp
// current/decomp/KSA/StaticMeshRenderable.cs L29
	public bool Visible { get; set; } = true;
```

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Transpiler applied | `[StarMapAllModsLoaded]` | `startup:all-mods-loaded` |
| Flag toggled | ImGui button from `[StarMapAfterGui]` | `frame:draw-ui` |
| Patched call runs | `KittenEva.UpdateRenderData` and `IVASeat.UpdateSeatedRenderable` inside `Program.RenderGame` / `RenderViewport`, once per view | `frame:render` |

Kittens are not updated in `UpdateRenderingResources`; their render data is built per viewport at the start of command recording, and `Draw(view)` only enqueues instances that the prepass and colour passes consume later. A toggle set in the GUI phase therefore applies to the same frame, in every view. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the gate and the state.**

```csharp
// humble-arteest.lib/KittenVisorPatches.cs L14-L21
    private static MethodInfo? _original;
    private static MethodInfo? _transpiler;

    public static bool IsApplied { get; private set; }
    public static bool Hidden { get; set; }
    public static string? LastError { get; private set; }

    public static void Apply(Harmony harmony)
```

2. **Patch `UpdateRenderData` by name** (single overload) with the transpiler. On any exception leave `IsApplied` false, force `Hidden = false` and keep the message for the UI, so a game change disables the button instead of crashing:

```csharp
// humble-arteest.lib/KittenVisorPatches.cs L21-L40
    public static void Apply(Harmony harmony)
    {
        if (IsApplied) return;
        LastError = null;
        try
        {
            _original = AccessTools.Method(typeof(KittenRenderable), nameof(KittenRenderable.UpdateRenderData))
                ?? throw new MissingMethodException("KittenRenderable.UpdateRenderData not found.");
            _transpiler = AccessTools.Method(typeof(KittenVisorPatches), nameof(Transpile));
            harmony.Patch(_original, transpiler: new HarmonyMethod(_transpiler));
            IsApplied = true;
            Console.WriteLine("humble-arteest: Kitten visor toggle patch applied");
        }
        catch (Exception ex)
        {
            Hidden = false;
            LastError = "Visor toggle unavailable: " + ex.GetBaseException().Message;
            Console.WriteLine($"humble-arteest: {LastError}");
        }
    }
```

3. **Match the receiver, not just the callee.** The helmet shell is drawn with the same `Draw(view)` call one line earlier, so match the three-instruction window `ldfld Helmet.VisorMesh`, any `ldloc`, `call StaticMeshRenderable.Draw(ViewHandle)`. Throw unless there is exactly one match. Keep the instruction object (so labels and exception blocks stay valid) and only swap opcode and operand; the stack effect is the same (consume mesh and view, return void):

```csharp
// humble-arteest.lib/KittenVisorPatches.cs L53-L87
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
```

4. **Toggle the flag from the UI** and force it off whenever the panel deactivates, on dispose and on a save reset:

```csharp
// humble-arteest.lib/KittenColorSubmod.cs L89-L101
    private static void RenderVisorControl()
    {
        ImGui.BeginDisabled(!KittenVisorPatches.IsApplied);
        if (ImGui.Button(KittenVisorPatches.Hidden ? "Show visor glass##kc_visor" : "Hide visor glass##kc_visor"))
            KittenVisorPatches.Hidden = !KittenVisorPatches.Hidden;
        ImGui.EndDisabled();
        ImGui.SetItemTooltip("Hides visor glass on all kittens, including newly spawned kittens.\n" +
            "Show restores the normal visor draw. Helmet and material colors are unchanged.");
        ImGui.SameLine();
        ImGui.TextDisabled(KittenVisorPatches.Hidden ? "Visors hidden" : "Visors shown");
        if (!KittenVisorPatches.IsApplied)
            ImGui.TextWrapped(KittenVisorPatches.LastError ?? "Visor toggle patch is not installed.");
        ImGui.Spacing();
```

5. **Check the result.** Press **Hide visor glass**: the visor disappears on every kitten, including kittens that spawn later and seated crew; the helmet shell and eyes remain. **Show** restores it.

## Complete example

- [humble-arteest.lib/KittenVisorPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenVisorPatches.cs) - `Apply`, `Remove`, the transpiler and `DrawVisor`.
- [humble-arteest.lib/KittenColorSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/KittenColorSubmod.cs) - the button and the resets.
- Feature page: [Kitten visor toggle](../../../../mods/unscience/features/humble-arteest-kitten-visor-toggle.md).

## Variations and gotchas

- **5482 signature change.** Rev 5474 bucketed meshes per view: `Draw()` became `Draw(ViewHandle)` and a local `view` is loaded between the field load and the call, so the older "adjacent `ldfld` + `Draw()`" pattern stopped matching. Unchanged in build 2026.10.10.5554.
- **The flag is global.** It applies to every kitten and every view; for one view only, see [hide a kitten's body from one viewport](../hide-a-kitten-body-from-one-viewport/overview.md).
- **Persisted state.** In Unscience the flag is saved with the humble-arteest participant (`HideVisor`); a standalone host keeps it for the session.
- **Visual acceptance.** The authors' native checks (several kittens, spawn while hidden, view changes) were still pending at the pinned ref.

## See also

- [Topic overview](overview.md); siblings: [approach 1](tint-and-restyle-kitten-appearance-1.md), [approach 2](tint-and-restyle-kitten-appearance-2.md), [approach 3](tint-and-restyle-kitten-appearance-3.md), [approach 5](tint-and-restyle-kitten-appearance-5.md)
- Mod feature: [Kitten visor toggle](../../../../mods/unscience/features/humble-arteest-kitten-visor-toggle.md), [Kitten Color](../../../../mods/unscience/features/humble-arteest-kitten-color.md)
- [Isolate patch failures](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Hide a kitten's body from one viewport](../hide-a-kitten-body-from-one-viewport/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
