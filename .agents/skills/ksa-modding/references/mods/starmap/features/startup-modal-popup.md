---
type: Mod Feature
title: Startup modal popup with a self-pumped frame
description: StarMap shows its restart question during KSA's loading phase by subclassing KSA.Popup and running its own GLFW poll, ImGui frame and Vulkan submit loop, copied from the game's ConfirmMods dialog
mod: starmap
category: ui
techniques: [imgui-window, startup-frame-pump, vulkan-command-buffer]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types:
  - KSA.Popup
  - KSA.PopupButton
  - KSA.ConfirmMods
  - KSA.SetupTaskBase
  - KSA.ImGuiHelper
  - KSA.ConsoleStyle
  - KSA.Program
  - Core.Renderer
tags: [starmap, imgui, popup, modal, startup, vulkan, loading-screen]
---

During KSA's loading phase there is no frame loop: the `Program` constructor runs to completion
before `App.Run` starts calling `OnFrame`, and the loading screen draws itself by pumping single
frames between steps. To ask the player a question at that point, StarMap does what KSA itself
does for its "MOD FOUND" dialog: it subclasses the game's `Popup` class for the content and runs
its own minimal frame (poll input, ImGui frame, one render pass with only ImGui draw data, submit)
in a `while (Show)` loop. `ConfirmRestart` is a near line-for-line copy of `KSA.ConfirmMods`.[^ksa-confirmmods]

## How it works

**Content: a `KSA.Popup` subclass.** The `Popup` base constructor gives each instance a unique
`WindowId` and adds it to a static list; `Popup.DrawAll()` draws every active popup and drops
inactive ones. `ConfirmRestartPopup` overrides `OnDrawUi` with the game's console-style modal
helpers (`BeginConsoleModal`, `ConsoleStyle.BeginBody`, `ConsoleStyle.PushWidgetStyle`,
`CenteredWrappedText`, `EndConsoleBody`, `DrawConsoleButtonRow`, `EndConsoleModal`) and two
`PopupButton`s created with `Popup.CreateButton`, so it looks exactly like KSA's own dialogs.[^ksa-popup][^sm-confirmrestartpopup]

**Host: a frame pump.** `ConfirmRestart` (derived from `SetupTaskBase`, which only adds text
helpers) grabs `Program.GetRenderer()` and, in `OnFrame`:[^sm-confirmrestart]

1. Returns unless `Program.IsMainThread()`.
2. `Glfw.PollEvents()`; if the window should close, `Environment.Exit(0)`.
3. `ImGuiBackend.NewFrame()`, `ImGui.NewFrame()`, `ImGuiHelper.StartFrame()`.
4. `DrawUi`: `ImGuiHelper.BlankBackground(ImGuiHelper.GetMainOverlayDrawList())` to cover the
   screen, then `Popup.DrawAll()`.
5. `ImGui.Render()`, plus `UpdatePlatformWindows` / `RenderPlatformWindowsDefault` when multi-viewport
   is enabled.
6. `_renderer.TryAcquireNextFrame()`; on failure `PartialRebuild`.
7. Record one render pass on `Program.MainPass.Pass` into the acquired framebuffer, call
   `ImGuiBackend.Vulkan.RenderDrawData(commandBuffer)`, end, and `_renderer.TrySubmitFrame()`; on
   failure `PartialRebuild` (`Renderer.Rebuild`, `WaitIdle`, refresh `Program.MainPass.Pass`,
   `Program.ScheduleRendererRebuild()`).

The caller (`ModLibraryPatches.BeforePrepareAll`) loops `while (confirmRestart.Show)
confirmRestart.OnFrame();`. The buttons set `Show = false` and `Restart` to the choice.[^sm-modlibrarypatches]

## Lifecycle and timing

It runs on the main thread inside the `Program` constructor (`startup:asset-load`, from a prefix on
`ModLibrary.PrepareAll`). Preconditions the pump depends on, all created earlier in the
constructor: the `Renderer` (line 794), `Program.MainPass` (line 825), the ImGui context and
backend (lines 842-852) and fonts (line 853).[^ksa-program] The technique therefore works from
`StarMapImmediateLoad`, `StarMapAllModsLoaded` or any Harmony patch on code that runs during the
constructor after those lines, but not from `StarMapBeforeMain` (no renderer yet). During the frame
loop it is unnecessary and would collide with the game's own ImGui frame: there, constructing a
`Popup` subclass is enough, because `Program.OnDrawUiFrame` calls `Popup.DrawAll()` every frame
while the HUD is shown.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs#L21-L98) | Frame pump and rebuild handling |
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs#L7-L50) | Popup content and buttons |
| [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L46-L50) | Modal loop |

## Snippets

```csharp
// StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs L13-L49
private static readonly PopupButton<ConfirmRestartPopup> PopupButtonContinue = CreateButton("Continue", (Action<ConfirmRestartPopup>)(popup =>
{
    popup.Active = false;
    popup.UI.Restart = false;
    popup.UI.Show = false;
}));
private static readonly PopupButton<ConfirmRestartPopup> PopupButtonRestart = CreateButton("Restart", (Action<ConfirmRestartPopup>)(popup =>
{
    popup.Active = false;
    popup.UI.Restart = true;
    popup.UI.Show = false;
}));

private static readonly PopupButton<ConfirmRestartPopup>[] ButtonMatrix = [
    PopupButtonContinue,
    PopupButtonRestart
];

private ConfirmRestartPopup(ConfirmRestart ui)
{ 
    _title = "Game requires restart";
    UI = ui;
}

public static ConfirmRestartPopup Create(ConfirmRestart ui) => new(ui);

protected override void OnDrawUi()
{
    if (!BeginConsoleModal(WindowId.AsSpan(), _title.AsSpan(), SIZE_UV))
        return;
    ConsoleStyle.BeginBody();
    ConsoleStyle.PushWidgetStyle();
    CenteredWrappedText("New mods have been enabled after starting KSA.\nThe game needs to be restarted for these mods to be loaded in StarMap.".AsSpan());
    EndConsoleBody();
    DrawConsoleButtonRow<ConfirmRestartPopup>(this, ButtonMatrix);
    EndConsoleModal();
}
```

```csharp
// StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs L39-L73
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
        if (EnumEx.IsSet((int)ImGui.GetIO().ConfigFlags, 1024 /*0x0400*/))
        {
            ImGui.UpdatePlatformWindows();
            ImGui.RenderPlatformWindowsDefault();
        }
        (FrameResult result, AcquiredFrame acquiredFrame1) = _renderer.TryAcquireNextFrame();
        AcquiredFrame acquiredFrame2 = acquiredFrame1;
        if (result != 0)
        {
            PartialRebuild();
        }
        else
        {
            acquiredFrame1 = acquiredFrame2;
            (FrameResources resources, CommandBuffer commandBuffer) = acquiredFrame1;
            VkSubpassContents contents = VkSubpassContents.Inline;
            VkRenderPassBeginInfo pRenderPassBegin = new VkRenderPassBeginInfo()
            {
                RenderPass = Program.MainPass.Pass,
```

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `Popup` (ctor, `Active`, `WindowId`, `OnDrawUi`, `DrawAll`, `CreateButton`, console modal helpers) | [current/decomp/KSA/Popup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Popup.cs#L12-L74) | Popup content and registration |
| `ConfirmMods.OnFrame` | [current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs#L62-L114) | The game's original of this frame pump |
| `ConfirmModPopup` | [current/decomp/KSA/ConfirmModPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmModPopup.cs#L6-L50) | The game's original of the popup content[^ksa-confirmmodpopup] |
| `Program.GetWindow`, `GetRenderer`, `IsMainThread` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L552-L604) | Window, renderer and thread check |
| `Program.MainPass` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L454) | Swapchain render pass and clear values used for the ImGui-only pass |
| `Program.ScheduleRendererRebuild` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5248-L5251) | Asks the main loop to rebuild after a swapchain failure |
| `Renderer.TryAcquireNextFrame`, `TrySubmitFrame`, `Rebuild`, `WaitForPreviousPresent` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L336-L532) | Swapchain acquire and submit[^core-renderer] |

## Pitfalls and version notes

- **Drift from the game's copy.** In build 2026.10.10.5554, `ConfirmMods.OnFrame` also calls
  `_renderer.WaitForPreviousPresent(out _)` before polling and sets `_renderer.InputSampleTicks`;
  StarMap's copy does neither. The practical effect was not verified; when copying this pattern,
  copy it from the current decompiled `ConfirmMods` rather than from StarMap.
- **Closing the window exits the process** (`Environment.Exit(0)`), skipping all shutdown code.
- **The constructor draws one frame before `_popup` exists** (it calls `OnFrame()` before
  `ConfirmRestartPopup.Create`), so the first frame shows only the blank background.
- **Main thread only.** The pump returns immediately on other threads, so a `while (Show)` loop on
  a worker thread would spin forever.

## See also

- [Restart when new mods are enabled](new-mods-restart-flow.md)
- [Startup lifecycle hooks](startup-lifecycle-hooks.md)
- [Relaunch the game from a mod with a PrepareAll prefix and Process.Start](../../../howto/getting-started/lifecycle/clean-up-on-game-exit/clean-up-on-game-exit-2.md) - how-to article demonstrating this feature
- [Loading dialog: a modal question modelled on ConfirmMods, with its own frame pump](../../../howto/ui/windows/show-a-dialog-during-loading/show-a-dialog-during-loading-1.md) - how-to article demonstrating this feature

[^sm-confirmrestart]: StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs
[^sm-confirmrestartpopup]: StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs
[^sm-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^ksa-confirmmods]: KSA.ConfirmMods (decompiled)
[^ksa-confirmmodpopup]: KSA.ConfirmModPopup (decompiled)
[^ksa-popup]: KSA.Popup (decompiled)
[^ksa-program]: KSA.Program (decompiled)
[^core-renderer]: Core.Renderer (decompiled)
