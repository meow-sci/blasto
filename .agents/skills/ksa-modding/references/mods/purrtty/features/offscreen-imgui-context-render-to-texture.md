---
type: Mod Feature
title: Render-to-texture with a second ImGui context and Vulkan backend
description: Draw ImGui UI into a mod-owned off-screen Vulkan image by creating a secondary ImGui context that shares the game's font atlas, a second instance of KSA's ImGuiBackendVulkanImpl bound to a mod render pass, and a mod-owned command pool, ring of fences and queue submit
mod: purrtty
category: rendering
techniques: [vulkan-texture, imgui-window, imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types:
  - KSA.ImGuiBackendVulkanImpl
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiContextPtr
  - Core.Renderer
  - Brutal.VulkanApi.Abstractions.ImageEx
  - Brutal.VulkanApi.Queue
  - KSA.Program
tags: [vulkan, imgui, render-to-texture, offscreen, in-world, command-buffers]
---

purrTTY can put a live terminal into the 3D world: on a panel attached to a part of the rocket, or
on a HUD-like billboard in front of the camera. Each in-world terminal draws the same ImGui terminal
UI as the 2D windows, but into its own Vulkan image instead of the screen, and a world-space quad
samples that image (see [quad pipeline](./world-space-quad-pipeline-custom-shader.md)). This page
covers the render-to-texture half, which is a reusable recipe for any "ImGui UI on a texture" mod:
a monitor in a cockpit, a sign on a building, a screen on a part.

## How it works

Per in-world terminal (`InWorldTerminalInstance`), built in this order:[^inworld-instance]

| Object | File | Vulkan / ImGui objects created |
|---|---|---|
| `OffscreenRenderTarget` | `OffscreenRenderTarget.cs` | Color image `R8G8B8A8UNorm` and depth image (`renderer.DepthFormat`) through `renderer.Allocator.CreateImage(ImageEx.CreateInfo)`, two `VkImageView`s, a classic `VkRenderPass` (color clear/store with `FinalLayout = ShaderReadOnlyOptimal`, depth clear/store, one external-to-subpass dependency), a `VkFramebuffer`, a linear clamped `VkSampler` (`Presets.Sampler.SamplerLinearClamped`) |
| `OffscreenImGuiContext` | `OffscreenImGuiContext.cs` | `ImGui.CreateContext(mainIo.Fonts)`: a second context sharing the main font atlas, `DisplaySize` = texture extent, no `.ini`, keyboard nav on |
| `OffscreenImGuiBackend` | `OffscreenImGuiBackend.cs` | A second `ImGuiBackendVulkanImpl` (`RenderPass` = the target's pass, `SampleCount = 1`, `MinImageCount = ImageCount = 2`, `DescriptorPoolSize = 256`), constructed while the secondary context is current because the ctor installs itself into the current context's IO |
| `PerFrameRenderer` | `PerFrameRenderer.cs` | A `VkCommandPool` on the graphics family (`Transient` + `ResetCommandBuffer`), two primary command buffers, two fences created signaled |
| `InWorldTerminalRenderer` | `purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs` | Its own `SessionManager`, shell and theme; draws with the same `FrameGridRenderer` as 2D windows |
| `InWorldQuad` | `Display/InWorldQuad.cs` | One descriptor pool and set pointing at the target's view and sampler |

The texture size is derived from the requested grid: `cols x cellWidth` by `rows x cellHeight`,
measured on the live atlas and clamped to 256..4096 px. That is why creation must happen inside an
ImGui frame.

**Each frame** (`PerFrameRenderer.Frame`):

1. Wait for and reset this ring slot's fence (the submit from two renders ago).
2. With the secondary context current (`OffscreenImGuiContext.With` saves, switches and restores
   the current context in `try/finally`): set `DeltaTime` and `DisplaySize`, `ImGui.NewFrame()`,
   `BuildUi()` (a borderless full-size window whose draw list gets the theme background at
   `BackgroundOpacity` and the terminal grid), `ImGui.Render()`, `GetDrawData()`.
3. Record: `BeginRenderPass` (clear color to transparent `(0,0,0,0)`, depth to 1), then
   `backend.RenderDrawData(drawData, cmd)` with the secondary context current and
   `drawData.Textures` temporarily nulled, then `EndRenderPass`.
4. `renderer.Graphics.Submit(cmd, fence)` with no semaphores; `Queue.Submit` takes an internal
   lock, so this is safe beside the game's own submits.[^ksa-queue]

**Skip when unchanged.** `InWorldTerminalInstance.Frame` always ticks the terminal (the PTY inbox
must drain) but re-records and submits only when `TickAndCheckDirty` reports a new frame
generation, new images, a focus change, a forced redraw, or at least 1 s since the last render; the
quad keeps sampling the previous texture otherwise.[^inworld-renderer]

**Shared atlas, borrowed textures.** The secondary context draws with the main atlas's textures,
whose descriptor sets belong to the main backend's pool. Nulling `drawData.Textures` during the
secondary `RenderDrawData` stops the secondary backend from creating or destroying atlas textures
it does not own; KSA's backend treats `TextureId` as a raw `VkDescriptorSet` and binds whatever set
a draw carries with its own (compatible) layout, so main-pool sets work in the secondary pass.
Kitty images in in-world terminals are registered with the main backend for the same reason
(gotcha 35). If the main backend has not uploaded a newly added glyph page yet, the secondary
render throws `ImException`; the frame is skipped and the next one succeeds.[^per-frame][^gotchas]

**Creation and management.** `InWorldTerminalManager` (coordinator) builds instances from an
`InWorldTerminalRecord` (name, shell, cols/rows, theme, part or billboard placement) through the
non-modal "In-World Terminals..." dialog, the Layouts manager, or the `PURRTTY_INWORLD=1` dev
switch; instances are session-only and self-register in the named terminal registry.[^inworld-manager][^inworld-ui]

## Lifecycle and timing

| Step | When |
|---|---|
| Coordinator `Initialize` (no GPU) | `startup:all-mods-loaded` (`TerminalMod.TryInitializeInWorld`) |
| Instance creation (GPU allocation, font measurement) | `frame:draw-ui`, from the dialog or layout apply inside `TerminalMod.OnAfterUi`, because it needs an active ImGui frame for the cell measurement and, for the first instance, `Program.OffscreenTarget` (created after `StarMapAllModsLoaded`) for the shared quad pipeline |
| Off-screen UI build, record, submit | `frame:draw-ui`, `InWorldTerminalManager.OnAfterGui` -> `instance.Frame`, called from `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`), between the game's `ImGui.NewFrame` and `ImGui.Render` for the main context, after the next physics step was dispatched; only in frames where the HUD is drawn and the content changed[^terminal-mod] |
| Quad sampling the texture | `frame:render`, later the same frame, inside the main scene command buffer (see [translucency postfix](./draw-after-translucency-pass-postfix.md)) |

The secondary context is switched in and out while the main context is mid-frame; every switch
restores the previous context, and the secondary context is never destroyed while current
(KSA's GLFW backend dereferences `ImGui.GetIO()` every frame).

The off-screen submit precedes the game's frame submit on the same queue. No semaphore or explicit
barrier links them; the code relies on submission order plus the render pass's final layout
transition (class comment in `PerFrameRenderer`). Vulkan's implicit external dependency at the end
of a render pass does not by itself make color writes visible to a later fragment-shader read, so a
mod copying this should add a subpass-0-to-external dependency (color-attachment write to
fragment-shader read) to be spec-correct; purrTTY works in practice without it (not validated here).

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs#L84-L166) | Build order and per-frame dirty gating |
| [purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs#L76-L228) | Images, views, render pass, framebuffer, sampler |
| [purrTTY.GameMod/InWorld/OffscreenImGuiContext.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenImGuiContext.cs#L43-L154) | Shared-atlas context, `With`, safe destroy |
| [purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs#L40-L82) | Second `ImGuiBackendVulkanImpl` |
| [purrTTY.GameMod/InWorld/PerFrameRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/PerFrameRenderer.cs#L139-L266) | Fence ring, NewFrame/Render, record, submit |
| [purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs#L144-L304) | Tick, dirty check, content draw with opacity |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L109-L157) | Shared quad resource and instance creation |
| [purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs) | Create/list/configure dialog |

## Snippets

```csharp
// purrTTY.GameMod/InWorld/PerFrameRenderer.cs L152-L188
        // 2. Build the secondary-context UI and bake the draw lists. Every ImGui
        //    call here MUST happen with the secondary context current.
        ImDrawDataPtr drawData = default;
        _ctx.With(() =>
        {
            var io = ImGui.GetIO();
            io.DeltaTime   = (float)dt;
            io.DisplaySize = new float2(_target.Extent.Width, _target.Extent.Height);

            ImGui.NewFrame();
            try
            {
                BuildUi?.Invoke();
            }
            finally
            {
                // ImGui requires a matching Render() for every NewFrame(); skipping
                // it on a BuildUi exception would leave the context in a bad state.
                ImGui.Render();
            }
            drawData = ImGui.GetDrawData();
        });

        // 3. Reset & re-record the slot's command buffer.
        cmd.Reset(VkCommandBufferResetFlags.None);
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        RecordRenderPass(cmd, drawData);

        cmd.End();

        // 4. Submit on the same graphics queue the main render uses. Submit is
        //    internally locked, so this is safe vs. KSA's own submissions.
        Span<CommandBuffer>        cmds     = stackalloc CommandBuffer[1] { cmd };
        Span<VkSemaphore>          none     = default;
        Span<VkPipelineStageFlags> noStages = default;
        _renderer.Graphics.Submit(none, noStages, cmds, none, fence);
```

```csharp
// purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs L40-L62
    public OffscreenImGuiBackend(Renderer renderer, VkRenderPass renderPass,
                                 int minImageCount, int imageCount,
                                 int descriptorPoolSize = 256)
    {
        ArgumentNullException.ThrowIfNull(renderer);

        // The off-screen terminal target is single-sample (no MSAA) — ImGui text
        // needs none. (The world-space quad pipeline, by contrast, matches the
        // scene pass's MSAA in a later phase.) The backend asserts
        // MinImageCount >= 2, ImageCount >= MinImageCount, DescriptorPoolSize > 0.
        Impl = new ImGuiBackendVulkanImpl(new ImGuiBackendVulkanImpl.CreateInfo
        {
            Device             = renderer.Device,
            GraphicsQueue      = renderer.Graphics,
            RenderPass         = renderPass,
            SubPass            = 0,
            MinImageCount      = minImageCount,
            ImageCount         = imageCount,
            SampleCount        = VkSampleCountFlags._1Bit,
            DescriptorPoolSize = descriptorPoolSize,
            // MinAllocationSize: leave default; KSA's backend handles a zero default.
        });
    }
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ImGuiBackendVulkanImpl(in CreateInfo)`, `CreateInfo { Device, GraphicsQueue, RenderPass, SubPass, MinImageCount, ImageCount, SampleCount, DescriptorPoolSize }`, `RenderDrawData(ImDrawDataPtr, CommandBuffer)`, `Dispose` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L20-L40) | The game's ImGui renderer, instantiated a second time[^ksa-imgui-vulkan] |
| `ImGui.CreateContext(ImFontAtlasPtr)`, `GetCurrentContext`, `SetCurrentContext`, `DestroyContext`, `NewFrame`, `Render`, `GetDrawData` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5418-L5436) | Secondary context with a shared atlas[^ksa-imgui] |
| `Program.GetRenderer()`, `Renderer.Device`, `.Allocator`, `.Graphics`, `.DepthFormat` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L557-L560) | Device, allocator and graphics queue |
| `Queue.Submit` (locked) | [current/decomp/Brutal.VulkanApi/Queue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/Queue.cs#L77-L84) | Thread-safe submit beside the game's |

## Pitfalls and version notes

- Construct and dispose the secondary backend with the secondary context current; it asserts that
  no renderer backend is already installed in the current context's IO.
- Never let the secondary backend manage atlas textures (null `drawData.Textures` while rendering).
- Use `R8G8B8A8UNorm`, not SRGB: the quad shader gamma-decodes the texel itself, and an SRGB target
  double-decodes and looks dark (gotcha 27).
- Each instance costs one extra render pass and queue submit per changed frame plus about 4 MB of
  color at 1024x1024; bound the instance count (gotcha 27).
- `ImageEx` is a readonly struct whose `Dispose` throws on `default`; hold allocations as `ImageEx?`
  (gotcha 37, see [teardown discipline](./vulkan-resource-teardown-discipline.md)).
- While the HUD is hidden (`Program.DrawUI == false`) `StarMapAfterGui` does not run, so in-world
  textures stop updating and their sessions stop draining, while the quads keep drawing the last
  texture (inferred from the call paths).
- KSA 5154 moved the scene to dynamic rendering and deleted `KSA.RenderTarget`/`Framebuffer`;
  purrTTY now owns its classic render pass because KSA's ImGui backend still takes a `VkRenderPass`.
- `plans/GAME_SPACE_QUAD_PLAN.md` describes the phased design. Its status line still says nothing is
  implemented; the code at this ref implements all of the above.[^quad-plan]

## See also

- [World-space quad pipeline with a custom shader](./world-space-quad-pipeline-custom-shader.md)
- [Draw after the translucency pass](./draw-after-translucency-pass-postfix.md)
- [Anchor the quad to a part or the camera](./anchor-quad-to-part-or-camera.md)
- [Ray-pick focus for world quads](./ray-pick-focus-for-world-quads.md)
- [Vulkan resource teardown discipline](./vulkan-resource-teardown-discipline.md)
- [Terminal grid drawn with ImGui draw lists](./terminal-grid-drawlist-renderer.md)
- [purrTTY overview](../overview.md)
- [Secondary ImGui context sharing the game's atlas, second backend, fence-ring submission](../../../howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-1.md) - how-to article demonstrating this feature
- [ImGui rendered to a texture and drawn as a world-space quad](../../../howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-3.md) - how-to article demonstrating this feature
- [Mod command pool, fence ring and locked queue submit](../../../howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-2.md) - how-to article demonstrating this feature

[^inworld-instance]: purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs
[^ksa-queue]: Brutal.VulkanApi.Queue (decompiled, build 2026.10.10.5554)
[^inworld-renderer]: purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs
[^per-frame]: purrTTY.GameMod/InWorld/PerFrameRenderer.cs
[^gotchas]: purrTTY docs/gotchas.md
[^inworld-manager]: purrTTY.GameMod/InWorld/InWorldTerminalManager.cs
[^inworld-ui]: purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^ksa-imgui-vulkan]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^quad-plan]: purrTTY plans/GAME_SPACE_QUAD_PLAN.md
