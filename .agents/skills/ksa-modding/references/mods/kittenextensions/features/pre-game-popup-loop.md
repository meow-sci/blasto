---
type: Mod Feature
title: Pre-game ImGui popup loop and fatal patch errors
description: KittenExtensions shows its debugger and fatal error dialog before the game loop exists by subclassing KSA.Popup and pumping its own GLFW, ImGui and Vulkan frame in a loop copied from SelectSystem, then exits after a patch error
mod: kittenextensions
category: ui
techniques: [imgui-window, modal-loop, vulkan-render-pass, fail-fast]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types:
  - KSA.Popup
  - KSA.SetupTaskBase
  - KSA.SelectSystem
  - KSA.ConfirmMods
  - KSA.Program
  - KSA.ImGuiHelper
  - KSA.ImGuiBackend
  - KSA.GameSettings
  - Core.Renderer
tags: [imgui, popup, modal, vulkan, startup, error-handling, glfw]
---

KittenExtensions needs UI at a moment when KSA has no frame loop yet: in the middle of the `Program`
constructor, inside `ModLibrary.PrepareAll`. The game itself solves this for its new-mod dialog
and its system picker by running a private loop that polls GLFW, builds one ImGui frame and submits one
Vulkan render pass containing only ImGui. KittenExtensions copies that loop into a small `PopupTask`
class (the source says "copied from SelectSystem") and uses it for two popups: the
[patch debug stepper](patch-debug-stepper.md) and the fatal "KittenExtensions Patch Error"
dialog.[^kx-utils] [^ksa-selectsystem]

## How it works

1. **A popup is a `KSA.Popup`.** `PatchDebugPopup` and `ErrorPopup` subclass the game's abstract
   `Popup`, implement `OnDrawUi`, and close by setting `Active = false`
   ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L12-L74)). Constructing one registers it, and
   `Popup.DrawAll` draws every active popup ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L46-L64)).[^ksa-popup]
2. **`PopupTask` wraps it** ([KittenExtensions/Patch/Utils.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Utils.cs#L266-L338)). It extends
   `SetupTaskBase`, grabs `Program.GetRenderer()`, and exposes `Show => popup.Active`. The caller
   loops `while (task.Show) task.OnFrame();`.
3. **One frame** (`OnFrame`): return unless on the main thread; `Glfw.PollEvents()`; if the window
   should close, `Environment.Exit(0)`; `ImGuiBackend.NewFrame()`, `ImGui.NewFrame()`,
   `ImGuiHelper.StartFrame()`; draw a blank background on the main overlay draw list and
   `Popup.DrawAll()`; `ImGui.Render()` and, with multi-viewports enabled, update and render platform
   windows; acquire a swapchain frame, begin `Program.MainPass.Pass` on its framebuffer, record
   `ImGuiBackend.Vulkan.RenderDrawData`, end, submit; on any acquire or submit failure rebuild the
   swapchain (`PartialRebuild`).
4. **Fatal errors.** `ErrorAndExit(error, elementLoc, strLoc)` writes the error to `stderr`, runs an
   `ErrorPopup` through `PopupTask` until the player presses "Exit", then calls
   `Environment.Exit(1)` ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L123-L131)). It is used for an
   unreadable `mod.toml`, an XML file that fails to import, an exception during the patch run and an
   executor error ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L77-L102),
   [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L141-L177)).[^kx-patcher]
5. **What the error dialog shows** ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L632-L861)): a
   red title, "Copy To Clipboard" and "Exit" buttons, a location line `Patch Error @ <path>` built from
   element names with `Id` predicates or positional indexes (or the file path for `mod.toml` and
   import errors), the exception text, and for element errors an abbreviated XML excerpt: the
   ancestors' opening tags, up to two collapsed siblings on each side of the failing element, and an
   `<!-- ERROR -->` marker above it.[^kx-patcher-debug]
6. **Layout helpers.** `ImGuiEx.Space` splits the available region (`SplitX`, `CutX`, `CutY`,
   `Indent`), `CenterTextCursor` centres titles, `IndentScope` pairs `Indent`/`Unindent` with `using`
   ([KittenExtensions/Patch/ImGuiEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.cs#L14-L85)).[^kx-imguiex]

## Lifecycle and timing

`startup:asset-load`, inside the KittenExtensions prefix on `ModLibrary.PrepareAll`. At that point
the `Program` constructor has created the window, the renderer, the main render pass and the ImGui
context, and has drawn its loading screen ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L885-L899)), but it
has not returned, so `program.Run()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1701-L1702)), which
drives `Program.OnFrame`, has not started.[^ksa-program] No StarMap frame hook
(`StarMapBeforeGui`, `StarMapAfterGui`, `StarMapAfterOnFrame`) can run yet, so a private loop is the
only way to show interactive UI. The loop is synchronous on the main thread and holds up the rest of
startup until the popup closes or the process exits.

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/Patch/Utils.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Utils.cs#L266-L338) | `PopupTask`: the copied startup frame loop |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L123-L131) | `ErrorAndExit`: popup then `Environment.Exit(1)` |
| [KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L632-L861) | `ErrorPopup`: location path, XML excerpt, buttons |
| [KittenExtensions/Patch/ImGuiEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.cs#L14-L85) | Region-splitting layout helpers |

## Snippets

```csharp
// KittenExtensions/Patch/Utils.cs L282-L302
    public unsafe void OnFrame()
    {
        if (!Program.IsMainThread())
            return;
        Glfw.PollEvents();
        if (Program.GetWindow().ShouldClose)
        {
            Environment.Exit(0);
        }
        else
        {
            ImGuiBackend.NewFrame();
            ImGui.NewFrame();
            ImGuiHelper.StartFrame();
            DrawUi();
            ImGui.Render();
            if (ImGui.GetIO().ConfigFlags.HasFlag(ImGuiConfigFlags.ViewportsEnable))
            {
                ImGui.UpdatePlatformWindows();
                ImGui.RenderPlatformWindowsDefault();
            }
```

```csharp
// KittenExtensions/Patch/Utils.cs L303-L337
            (FrameResult result, AcquiredFrame acquiredFrame) = renderer.TryAcquireNextFrame();
            if (result != FrameResult.Success)
                PartialRebuild();
            else
            {
                (FrameResources resources, CommandBuffer commandBuffer) = acquiredFrame;
                VkSubpassContents contents = VkSubpassContents.Inline;
                VkRenderPassBeginInfo pRenderPassBegin = new()
                {
                    RenderPass = Program.MainPass.Pass,
                    Framebuffer = resources.Framebuffer,
                    RenderArea = new VkRect2D(renderer.Extent),
                    ClearValues = (VkClearValue*)Program.MainPass.ClearValues.Ptr,
                    ClearValueCount = 2
                };
                commandBuffer.Reset();
                commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
                commandBuffer.BeginRenderPass(in pRenderPassBegin, contents);
                ImGuiBackend.Vulkan.RenderDrawData(commandBuffer);
                commandBuffer.EndRenderPass();
                commandBuffer.End();
                if (renderer.TrySubmitFrame() == 0)
                    return;
                PartialRebuild();
            }
        }
    }

    public void PartialRebuild()
    {
        renderer.Rebuild(GameSettings.GetPresentMode());
        renderer.Device.WaitIdle();
        Program.MainPass.Pass = renderer.MainRenderPass;
        Program.ScheduleRendererRebuild();
    }
```

```csharp
// KittenExtensions/Patch/Patcher.cs L123-L131
    private static Exception ErrorAndExit(string error, XPNodeRef elementLoc = default, string strLoc = null)
    {
        Console.Error.WriteLine(error);
        var errTask = new PopupTask(new ErrorPopup(error, elementLoc, strLoc));
        while (errTask.Show)
            errTask.OnFrame();
        Environment.Exit(1);
        throw new InvalidOperationException();
    }
```

## Game APIs touched

| Member | What it is for |
|---|---|
| `Popup`, `Popup.DrawAll`, `Popup.Active`, `PopupId` ([current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L12-L74)) | Base class and drawing of both popups |
| `SelectSystem.OnFrame` / `PartialRebuild` ([current/decomp/KSA/SelectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SelectSystem.cs#L49-L105)) | The game loop KittenExtensions copied |
| `Program.GetRenderer`, `GetWindow`, `IsMainThread`, `MainPass`, `ScheduleRendererRebuild` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs)) | Renderer, window and main pass for the private frame |
| `ImGuiHelper.BlankBackground`, `GetMainOverlayDrawList`, `StartFrame`, `SetCurrentWindowToCenter` | Background fill, frame setup, centring |

## Pitfalls and version notes

- **The copy has drifted.** In build 2026.10.10.5554 `SelectSystem.OnFrame` first calls
  `_renderer.WaitForPreviousPresent(out _)` and sets `_renderer.InputSampleTicks`, and its
  `PartialRebuild` also calls `ImGuiBackend.Vulkan.UpdateMainRenderPass(...)`
  ([current/decomp/KSA/SelectSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SelectSystem.cs#L49-L105)); `ConfirmMods` does the same
  ([current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs#L62-L116)). `PopupTask` does none of these. Whether that
  matters at run time was not tested; when copying the pattern, copy it from the build you target.[^ksa-confirmmods]
- **Exit is the only way out of an error.** A bad file in any installed mod (KittenExtensions reads
  disabled mods too) ends the session with this dialog. The stock `XmlLoader` only logs and returns
  `null` ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L20-L33)).[^ksa-xmlloader]
- **Closing the window exits with code 0** from inside `OnFrame`, even from the error dialog.

## See also

- [Patch debug stepper](patch-debug-stepper.md)
- [Combined GameData XML document](gamedata-document.md)
- [ImGui tree view widget](imgui-tree-view.md)
- [StarMap startup modal popup](../../starmap/features/startup-modal-popup.md)
- [Loading dialog: a PopupTask loop with ErrorAndExit](../../../howto/ui/windows/show-a-dialog-during-loading/show-a-dialog-during-loading-2.md) - how-to article demonstrating this feature

[^kx-utils]: KittenExtensions/Patch/Utils.cs
[^ksa-selectsystem]: KSA.SelectSystem (decompiled, build 2026.10.10.5554)
[^ksa-popup]: KSA.Popup (decompiled, build 2026.10.10.5554)
[^kx-patcher]: KittenExtensions/Patch/Patcher.cs
[^kx-patcher-debug]: KittenExtensions/Patch/Patcher.Debug.cs
[^kx-imguiex]: KittenExtensions/Patch/ImGuiEx.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-confirmmods]: KSA.ConfirmMods (decompiled, build 2026.10.10.5554)
[^ksa-xmlloader]: KSA.XmlLoader (decompiled, build 2026.10.10.5554)
