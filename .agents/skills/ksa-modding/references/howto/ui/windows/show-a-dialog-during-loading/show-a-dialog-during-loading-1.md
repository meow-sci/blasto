---
type: How-To
title: "Loading dialog: a modal question modelled on ConfirmMods, with its own frame pump"
description: Ask the player a question while mods are being prepared by subclassing Popup for the content and running a private poll, ImGui-frame and ImGui-only render pass loop until the answer arrives
category: ui
group: windows
topic: show-a-dialog-during-loading
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/starmap/features/startup-modal-popup]
techniques: [harmony-prefix, imgui-window]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types:
  - KSA.Popup
  - KSA.PopupButton
  - KSA.ConfirmMods
  - KSA.SetupTaskBase
  - KSA.ImGuiHelper
  - KSA.ImGuiBackend
  - KSA.Program
  - KSA.ModLibrary
  - RenderCore.Renderer
tags: [loading, modal, popup, frame-pump, vulkan, startup, restart, starmap]
---
During KSA's loading phase there is no frame loop: the `Program` constructor runs to completion before `Run` starts calling `OnFrame`, and the loading screen draws itself by pumping single frames between steps. To ask the player a question at that point, StarMap does what KSA itself does for its "MOD FOUND" dialog (`ConfirmMods`): it subclasses the game's `Popup` for the content and runs its own minimal frame - poll input, ImGui frame, one render pass with only ImGui draw data, submit - in a `while (Show)` loop. StarMap's `ConfirmRestart` is a near line-for-line copy of the game's `ConfirmMods`, used to ask whether to restart after new mods were enabled. Demonstrated by StarMap. Prerequisites: StarMap (to be loaded early) and Harmony for the patch.

## Background: how the game does it

- **The model.** `KSA.ConfirmMods` derives from `SetupTaskBase`, grabs `Program.GetRenderer()`, and its `OnFrame` does: main-thread check, `WaitForPreviousPresent`, `Glfw.PollEvents()`, exit on close, `ImGuiBackend.NewFrame()`, `ImGui.NewFrame()`, `ImGuiHelper.StartFrame()`, `DrawUi()` (a blank overlay background plus `Popup.DrawAll()`), `ImGui.Render()`, acquire a frame, record one render pass with only `ImGuiBackend.Vulkan.RenderDrawData`, submit, and rebuild the swapchain on failure.[current/decomp/KSA/ConfirmMods.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmMods.cs#L53-L114)[^ksa-confirmmods]
- **Where the game runs it.** In the `Program` constructor, after the renderer (L794), the main render pass (L825) and the ImGui context and backend (L842-L853) exist and the loading screen has been shown, the game creates `ConfirmMods` and loops `while (confirmMods.Show) confirmMods.OnFrame();` when new mods are present, then calls `ModLibrary.PrepareAll()`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L884-L899)[^ksa-program]
- **Popups.** The `Popup` base constructor registers the instance; `Popup.DrawAll()` draws every active popup. See [show alerts and popups in game](../show-alerts-and-popups-in-game/show-alerts-and-popups-in-game-2.md).

## Lifecycle and timing

The code runs on the main thread inside the `Program` constructor (`startup:asset-load`), from a Harmony prefix on `ModLibrary.PrepareAll`, which the game calls after its own `ConfirmMods` loop. Preconditions the pump depends on were all created earlier in the constructor: the `Renderer`, `Program.MainPass`, the ImGui context and backend, and fonts. The technique therefore works from `StarMapImmediateLoad`, `StarMapAllModsLoaded` or any patch on code that runs during the constructor after those lines, but not from `StarMapBeforeMain` (no renderer yet). The loop is synchronous and holds up the rest of startup until the popup closes or the process exits. During the frame loop it is unnecessary and would collide with the game's own ImGui frame; there, constructing a `Popup` subclass is enough. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Patch `ModLibrary.PrepareAll` with a prefix** and run the dialog first. The loop polls until the answer is set; the answer decides whether to restart:

```csharp
// StarMap.Core/Patches/ModLibraryPatches.cs L30-L50
[HarmonyPatch(nameof(ModLibrary.PrepareAll))]
[HarmonyPrefix]
public static void BeforePrepareAll()
{
    static bool wereNewModsEnabled()
    {
        foreach (var mod in ModLibrary.Manifest.Mods)
        {
            if (mod.New && mod.Enabled)
                return true;
        }
        return false;
    }

    if (ModLibrary.HasNewMods() && wereNewModsEnabled())
    {
        var confirmRestart = new ConfirmRestart();
        while(confirmRestart.Show)
        {
            confirmRestart.OnFrame();
        }
```

2. **Write the popup content** as a `Popup` subclass. Buttons set the answer and `Active = false`; `OnDrawUi` uses the console-modal helpers so it looks like the game's own dialogs:

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

3. **Write the host**: a class (derived from `SetupTaskBase`) that takes the renderer, draws a blank background on the main overlay draw list, calls `Popup.DrawAll()`, and renders one ImGui-only pass:

```csharp
// StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs L21-L59
public ConfirmRestart()
{
    this._renderer = Program.GetRenderer();
    Show = true;
    OnFrame();
    Current = this;

    _popup = ConfirmRestartPopup.Create(this);
}

public void DrawUi()
{
    if (!Show)
        return;
    ImGuiHelper.BlankBackground(ImGuiHelper.GetMainOverlayDrawList());
    Popup.DrawAll();
}

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
```

4. **Record the render pass and handle failure.** Begin the main render pass on the acquired framebuffer, draw only ImGui, submit; on any acquire or submit failure rebuild the swapchain:

```csharp
// StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs L60-L98
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
                Framebuffer = resources.Framebuffer,
                RenderArea = new VkRect2D(this._renderer.Extent),
                ClearValues = (VkClearValue*)Program.MainPass.ClearValues.Ptr,
                ClearValueCount = 2
            };
            commandBuffer.Reset();
            commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            commandBuffer.BeginRenderPass<CommandBuffer>(in pRenderPassBegin, contents);
            ImGuiBackend.Vulkan.RenderDrawData(commandBuffer);
            commandBuffer.EndRenderPass<CommandBuffer>();
            commandBuffer.End<CommandBuffer>();
            if (_renderer.TrySubmitFrame() == 0)
                return;
            PartialRebuild();
        }
    }
}

public void PartialRebuild()
{
    _renderer.Rebuild(GameSettings.GetPresentMode());
    _renderer.Device.WaitIdle();
    Program.MainPass.Pass = _renderer.MainRenderPass;
    Program.ScheduleRendererRebuild();
}
```

5. **Act on the answer.** StarMap restarts the process (`--restarted`) when the player chose Restart, then exits; otherwise loading continues:

```csharp
// StarMap.Core/Patches/ModLibraryPatches.cs L52-L74
if (confirmRestart.Restart)
{
    Console.WriteLine("StarMap - RESTARTING");
    Console.WriteLine("======================================");

    var mainModule = Process.GetCurrentProcess().MainModule;
    if (mainModule is null || Path.GetDirectoryName(mainModule.FileName) is not string starMapInstallLocation)
    {
        Console.WriteLine("Unable to get StarMap install location for restart.");
    }
    else {
        var startInfo = new ProcessStartInfo
        {
            FileName = mainModule.FileName,
            Arguments = "--restarted",
            WorkingDirectory = starMapInstallLocation,
        };

        Process.Start(startInfo);
    }

    Environment.Exit(0);
}
```

## Complete example

| File | Role |
|---|---|
| [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L30-L77) | Prefix, modal loop, restart |
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestartPopup.cs#L1-L49) | Popup content and buttons |
| [StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/UI/ConfirmRestart/ConfirmRestart.cs#L11-L98) | Frame pump |

## Variations and gotchas

- **The copy omits two lines of the game's pump.** StarMap's `OnFrame` has no `WaitForPreviousPresent` and no `InputSampleTicks` assignment, which `ConfirmMods.OnFrame` has (compare L68-L75 of the game's file); the loop still works for a short dialog, but copy the game's version if you see frame pacing trouble (inferred, not tested).
- **`PartialRebuild` differs too.** The game's also calls `ImGuiBackend.Vulkan.UpdateMainRenderPass`; StarMap's does not.
- **Blank the screen.** The overlay `BlankBackground` hides whatever the loading screen last drew.
- **Preconditions.** Without a renderer and an ImGui context, the pump crashes; do not call it from `StarMapBeforeMain`.
- **Version.** Checked against 2026.10.10.5554; `SetupTaskBase`, `ImGuiHelper` and the renderer API are the game's internal/public surface and may change.

## See also

- [Overview of show-a-dialog-during-loading](overview.md)
- [Approach 2: PopupTask loop with ErrorAndExit](show-a-dialog-during-loading-2.md)
- [Show alerts and popups in game](../show-alerts-and-popups-in-game/overview.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- Mod feature: [Startup modal popup](../../../../mods/starmap/features/startup-modal-popup.md)

[^ksa-confirmmods]: KSA.ConfirmMods (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
