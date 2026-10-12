---
type: Mod Feature
title: Screen stream frame capture
description: Capture the game's rendered scene without stalling by transpiling a hook into Program.RenderGame just before its command buffer ends, blitting the offscreen image into a small per-frame-slot staging buffer read back frames later, and streaming it as Kitty terminal graphics
mod: gatos
category: rendering
techniques: [harmony-transpiler, harmony-prefix, vulkan-buffer, background-thread, deferred-readback]
hooks: [harmony-transpiler KSA.Program.RenderGame, harmony-prefix KSA.GameSettings.UiPixelCulling]
lifecycle: [render:post-process, thread:background]
game_types: [KSA.Program, KSA.Rendering.RenderTarget, KSA.GameSettings, KSA.UiCoverageMaskSystem, Core.Renderer]
tags: [vulkan, capture, readback, transpiler, harmony, kitty-graphics, streaming]
---

`/sim/display/stream` is a live, downscaled, frame-rate-limited view of the game, encoded with the
Kitty terminal graphics protocol so `cat /sim/display/stream` shows the game inside any
Kitty-capable terminal, including a purrTTY tab. Under the gimmick is the reference technique for
reading back KSA frames without a device wait or a second queue submit, which an earlier
out-of-band approach showed can crash the game.

## How it works

**Injection** ([gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs#L63-L76),
[gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs#L214-L255)): a Harmony transpiler on the private
`Program.RenderGame` scans the IL backwards for the last call to a one-argument `End` declared in
the `Brutal.VulkanApi` namespace (the frame command buffer's `End()`), copies the instruction that
loads its receiver, and inserts `ldarg.0; <load commandBuffer>; call OnRenderGameRecorded` before
it, moving any branch labels onto the inserted code. If the site is not found it returns the IL
unchanged and the feature stays off. In 5554 the tail of `RenderGame` is: full resolve, grid,
gauges, a barrier taking the offscreen colour image to `SampledReadVfc`, KSA's own screenshot
capture hook, AA passes, the final composite with ImGui, the swapchain grab, then
`commandBuffer2.End()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4932-L4955)). At the hook the offscreen
colour is resolved, shader-readable, outside any render pass and free of UI.

**Capture** ([gatOS.GameMod/Game/Ksa/FrameCapture.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/FrameCapture.cs#L136-L183)): throttled to `display_fps`
and only while enabled and someone has the stream open. Commands are recorded into the engine's own
command buffer:

1. transition the offscreen image `SampledReadVfc -> TransferSrc` and a small per-slot scratch image
   to `TransferDst` with KSA's sync2 `TransitionImages2` and `ImageBarrierInfo.Presets`;
2. `BlitImage` with linear filtering into a `B8G8R8A8_UNORM` scratch image (downscale, float to
   byte and clamp in one GPU op);
3. `CopyImageToBuffer` into a host-visible staging buffer for this slot, then restore the offscreen
   layout.

The staging ring is indexed by `Program.ResourceFrameIndex` (0..`MaxFramesInFlight`-1). When a slot
comes round again the engine has already waited on that slot's fence, so the previous copy is
complete and can be read from the persistent mapping with no wait of gatOS's own. If the driver
lacks blit support for the format pair, a full-resolution copy plus CPU conversion is used.

**Encode** ([gatOS.SimFs/Display/DisplaySurface.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/DisplaySurface.cs), [gatOS.SimFs/Display/KittyEncoder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/KittyEncoder.cs)):
`SubmitFrame` on the render thread only swaps a buffer and signals; a worker task does the swizzle,
zlib, base64 and Kitty APC framing, publishing only the latest frame (slow readers skip ahead). Every
unit reuses one image and placement id with no delete, so the previous frame stays visible while the
next loads.

**UI culling.** KSA stamps near-plane depth under opaque ImGui windows (`UiCoverageMaskSystem`), which
would punch black holes into a capture taken before the UI composite. A prefix on
`GameSettings.UiPixelCulling()`, whose only caller is that system, reports `false` while the stream is
live, leaving the player's saved setting untouched ([gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs#L105-L160)).

## Lifecycle and timing

- `render:post-process`: the hook records at the very end of `RenderGame` on the main thread, after
  the scene is final and before submission; the copy executes on the GPU with the frame.
- Readback of slot N happens when slot N is reused (about `MaxFramesInFlight` frames later); the
  frame-in-flight fence wait the engine already performs guarantees completion.
- Encoding and delivery run on a worker task and 9p session threads (`thread:background`).
- Installed at `[StarMapAllModsLoaded]` (`InstallDisplayHook`); a capture fault disables it for the
  session; Vulkan objects are freed at unload.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs) | IL transpiler, capture gate, UI-culling prefix |
| [gatOS.GameMod/Game/Ksa/FrameCapture.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/FrameCapture.cs) | in-band blit, copy, slot ring, deferred readback |
| [gatOS.SimFs/Display/DisplaySurface.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/DisplaySurface.cs) | latest-frame feed and encode worker |
| [gatOS.SimFs/Display/KittyEncoder.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/KittyEncoder.cs) | Kitty graphics framing, keyframes, zlib |
| [gatOS.SimFs/Display/DisplayStreamFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Display/DisplayStreamFile.cs) | multi-reader continuous 9p file |
| [STREAM_PLAN.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/STREAM_PLAN.md) | design and the investigation history |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/DisplayRenderPatch.cs L214-L255
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        var callIdx = -1;
        for (var i = codes.Count - 1; i >= 1; i--)
        {
            if (codes[i].opcode == OpCodes.Call
                && codes[i].operand is MethodInfo mi
                && mi.Name == "End"
                && mi.GetParameters().Length == 1
                && mi.DeclaringType?.Namespace == "Brutal.VulkanApi")
            {
                callIdx = i;
                break;
            }
        }

        if (callIdx < 1)
        {
            ModLog.Log.Warn("Display hook: CommandBuffer.End() call not found in RenderGame; "
                            + "the screen stream is disabled (capture not injected).");
            return codes;
        }

        var receiverLoad = codes[callIdx - 1]; // loads commandBuffer2 (the End receiver) by value
        var hook = AccessTools.Method(typeof(DisplayRenderPatch), nameof(OnRenderGameRecorded));
        var inserts = new List<CodeInstruction>
        {
            new(OpCodes.Ldarg_0),                                      // Program this
            new(receiverLoad.opcode, receiverLoad.operand),           // commandBuffer2
            new(OpCodes.Call, hook),
        };

        // Carry any branch labels from the receiver load onto our first inserted instruction so a jump
        // to that position still runs the hook (then falls through into the original End sequence).
        inserts[0].labels.AddRange(receiverLoad.labels);
        receiverLoad.labels.Clear();

        codes.InsertRange(callIdx - 1, inserts);
        return codes;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.RenderGame(AcquiredFrame, double)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955) | transpiler target |
| `Program.MainViewport.OffscreenTarget.ColorImage`, `Extent` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L36-L38) | capture source |
| `Program.ResourceFrameIndex` (instance field) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L219-L219) | slot index |
| `Renderer.MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28-L28) | ring size |
| `GameSettings.UiPixelCulling()` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3308-L3311) | prefix target |
| `UiCoverageMaskSystem` | [current/decomp/KSA/UiCoverageMaskSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UiCoverageMaskSystem.cs) | the only caller of the setting |

## Pitfalls and version notes

- Never submit your own command buffer or call `WaitIdle` per frame to read back; record into the
  engine's buffer and read a slot when it is reused.
- Map staging buffers once and keep them mapped; re-mapping per frame can unmap a shared VMA block
  (repo notes).
- 5554 added a KSA screenshot-capture call (`_screenshotCapture.OnRenderGameCapture`) just before
  the AA passes; the transpiler still targets the final `End()`, which is unchanged.
- Rev 5154 moved offscreen rendering to dynamic rendering; `ColorImage` became nullable, which the
  capture checks.
- Continuous 9p files deliver data only when the reader's buffer fills; this suits video rates.
- Formal in-game soak checklists are still open in the repo.

## See also

- [The /sim file models](sim-filesystem-file-models.md)
- [SSH shell sessions into the guest](ssh-shell-session-into-guest.md)
- [World-space textured quad (thug_life)](thug-life-world-space-quad.md)
- [External API clients and SDK](external-api-clients.md)
- [gatOS overview](../overview.md)
- [Staging-buffer readback on slot reuse, background KittyEncoder, fixed image id](../../../howto/external-io/terminals/stream-the-game-screen-to-a-terminal/stream-the-game-screen-to-a-terminal.md) - how-to article demonstrating this feature
- [Transpiler before the final End, blit into per-slot staging buffers, readback on slot reuse](../../../howto/rendering/vulkan-passes/capture-rendered-frames/capture-rendered-frames-1.md) - how-to article demonstrating this feature

