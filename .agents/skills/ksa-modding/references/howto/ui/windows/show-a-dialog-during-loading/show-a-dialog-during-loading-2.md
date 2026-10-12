---
type: How-To
title: "Loading dialog: a PopupTask loop with ErrorAndExit"
description: Wrap any Popup in a small task that pumps frames during ModLibrary.PrepareAll so a fatal error can be shown with copyable text and the failing element before the process exits
category: ui
group: windows
topic: show-a-dialog-during-loading
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kittenextensions/features/pre-game-popup-loop]
techniques: [harmony-prefix, imgui-window]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types:
  - KSA.Popup
  - KSA.SetupTaskBase
  - KSA.SelectSystem
  - KSA.ImGuiHelper
  - KSA.ImGuiBackend
  - KSA.Program
  - RenderCore.Renderer
tags: [loading, popup, error-dialog, fail-fast, clipboard, frame-pump, kittenextensions]
---
KittenExtensions patches game XML while mods are being prepared, and needs UI at a moment when KSA has no frame loop yet: inside the `ModLibrary.PrepareAll` call in the `Program` constructor. It copies the game's startup frame pump into a small reusable `PopupTask` (the source comment says "copied from SelectSystem") that takes any `Popup`, and uses it for a fatal "KittenExtensions Patch Error" dialog that shows the failing element and a "Copy To Clipboard" button, then calls `Environment.Exit(1)`. This article is the reusable-wrapper, fail-fast variant of [the ConfirmMods-style pump](show-a-dialog-during-loading-1.md). Demonstrated by KittenExtensions. Prerequisites: StarMap and Harmony.

## Background: how the game does it

The game runs private frame pumps for `ConfirmMods` (before `ModLibrary.PrepareAll()`, only when new mods were found) and `SelectSystem` (after `PrepareAll`, `PreloadAssetBundles` and `PreloadLanguages`, only when the system selection is shown) inside the `Program` constructor; both loop `while (x.Show) x.OnFrame();`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L884-L909)[^ksa-program] They rely on the renderer (L794), `Program.MainPass` (L825), the ImGui context and backend (L842-L853) and fonts, all created before. `ImGuiHelper.BlankBackground(GetMainOverlayDrawList())` fills the screen and `Popup.DrawAll()` draws the active popups.[current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L1727-L1732)[^ksa-imguihelper]

## Lifecycle and timing

`startup:asset-load`, inside the KittenExtensions prefix on `ModLibrary.PrepareAll`. At that point the window, renderer, main render pass and ImGui context exist and the loading screen has been drawn, but the `Program` constructor has not returned, so `Run` (which drives `Program.OnFrame`) has not started. No StarMap frame hook (`StarMapBeforeGui`, `StarMapAfterGui`, `StarMapAfterOnFrame`) can run yet, so a private loop is the only way to show interactive UI. The loop is synchronous on the main thread and holds up startup until the popup closes or the process exits. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Patch `ModLibrary.PrepareAll` with a prefix**; this is where the mod's work (and therefore its errors) happens:

```csharp
// KittenExtensions/Patches.cs L21-L28
[HarmonyPatch(typeof(ModLibrary), nameof(ModLibrary.PrepareAll)), HarmonyPrefix]
internal static void ModLibrary_PrepareAll_Prefix()
{
  // PrepareManifest is called by StarMap, so we shouldn't need to do this here, but try anyways
  if (!ModLibrary.PrepareManifest())
    return;
  XmlPatcher.OnPrepare();
}
```

2. **Wrap a popup in a task.** `PopupTask` extends `SetupTaskBase`, grabs the renderer, exposes `Show => popup.Active`, and draws a blank background plus `Popup.DrawAll()`:

```csharp
// KittenExtensions/Patch/Utils.cs L266-L280
// copied from SelectSystem
public class PopupTask(Popup popup) : SetupTaskBase
{
    private readonly Renderer renderer = Program.GetRenderer();
    private readonly Popup popup = popup;

    public bool Show => popup.Active;

    public void DrawUi()
    {
        if (!Show)
            return;
        ImGuiHelper.BlankBackground(ImGuiHelper.GetMainOverlayDrawList());
        Popup.DrawAll();
    }
```

3. **Pump one frame.** The same poll, ImGui frame, render, acquire, record and submit sequence as the game's pumps (explained in [approach 1](show-a-dialog-during-loading-1.md)), with swapchain rebuild on failure:

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
// KittenExtensions/Patch/Utils.cs L331-L337
public void PartialRebuild()
{
    renderer.Rebuild(GameSettings.GetPresentMode());
    renderer.Device.WaitIdle();
    Program.MainPass.Pass = renderer.MainRenderPass;
    Program.ScheduleRendererRebuild();
}
```

4. **Fail fast.** Print the error to `stderr`, run an `ErrorPopup` through the task until the player presses Exit, then exit the process with code 1:

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

5. **Draw the error popup.** The popup builds a location line (`Patch Error @ <path>`) and an XML excerpt in its constructor. `OnDrawUi` opens a popup at 80% of the window size, centres it, draws a red title, a "Copy To Clipboard" button (`ImGui.SetClipboardText`), an "Exit" button that sets `Active = false`, and a scrolling child with the text:

```csharp
// KittenExtensions/Patch/Patcher.Debug.cs L827-L860
protected override void OnDrawUi()
{
  ImGui.SetNextWindowSize(float2.Unpack(Program.GetWindow().Size) * 0.8f);
  ImGui.OpenPopup(title);
  ImGui.BeginPopup(
    title, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.Popup);
  ImGuiHelper.SetCurrentWindowToCenter();

  var space = ImGuiEx.AvailableSpace();
  const string titleText = "KittenExtensions Patch Error";
  ImGuiEx.CenterTextCursor(space, titleText);
  ImGui.TextColored(new float4(1, 0, 0, 1), titleText);

  (_, space) = space.CutY();
  var (left, right) = space.SplitX();

  ImGui.SetCursorScreenPos(left.Start);
  if (ImGui.Button("Copy To Clipboard", new float2(left.Size.X, 0)))
    ImGui.SetClipboardText(error);
  ImGui.Separator();
  (_, space) = space.CutY();

  ImGui.SetCursorScreenPos(right.Start);
  if (ImGui.Button("Exit", new float2(right.Size.X, 0)))
    Active = false;

  ImGui.SetNextWindowPos(space.Start);
  ImGui.SetNextWindowSize(space.Size);
  ImGui.BeginChild("Error", windowFlags: ImGuiWindowFlags.HorizontalScrollbar);
  ImGui.TextColored(new float4(1, 0, 0, 1), error);
  ImGui.EndChild();

  ImGui.EndPopup();
}
```

## Complete example

| File | Role |
|---|---|
| [KittenExtensions/Patch/Utils.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Utils.cs#L266-L338) | `PopupTask`: the copied startup frame loop |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L123-L131) | `ErrorAndExit` |
| [KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L632-L860) | `ErrorPopup`: location, excerpt, buttons |

## Variations and gotchas

- **Exit is the contract.** The loop never returns control to a half-patched game; `ErrorAndExit` ends with `Environment.Exit(1)`.
- **Copy to the clipboard.** An error the player can paste into a bug report is the point of the dialog.
- **Same preconditions as approach 1.** No renderer, no pump; do not run it from `StarMapBeforeMain`.
- **The copy differs from the game's.** `PopupTask.PartialRebuild` omits `ImGuiBackend.Vulkan.UpdateMainRenderPass` and the pump omits `WaitForPreviousPresent`, which `ConfirmMods` has (compare the game's file); inferred harmless for a terminal error dialog.
- **Version.** Checked against 2026.10.10.5554; the pump depends on internal renderer APIs.

## See also

- [Overview of show-a-dialog-during-loading](overview.md)
- [Approach 1: ConfirmMods-style question](show-a-dialog-during-loading-1.md)
- [Show alerts and popups in game](../show-alerts-and-popups-in-game/overview.md)
- [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md)
- Mod feature: [Pre-game popup loop](../../../../mods/kittenextensions/features/pre-game-popup-loop.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
