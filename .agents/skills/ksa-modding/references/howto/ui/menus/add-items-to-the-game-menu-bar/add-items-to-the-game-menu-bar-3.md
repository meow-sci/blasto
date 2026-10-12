---
type: How-To
title: Decorate the menu bar itself with a transpiler snapshot and a hook prefix
description: Capture the ImGui cursor position inside Program.DrawMenuBar with an injected call, restore it in a prefix on DrawProgramMenusHook, and draw sliding colored words as BeginMenu items over filled backgrounds
category: ui
group: menus
topic: add-items-to-the-game-menu-bar
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/jplrepo-menu-bar-animation]
techniques: [harmony-transpiler, harmony-prefix, imgui-menu]
hooks: [harmony-transpiler KSA.Program.DrawMenuBar, harmony-prefix KSA.Program.DrawProgramMenusHook]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, Brutal.ImGuiApi.ImGui]
tags: [menu-bar, transpiler, cursor, animation, restyle, development-reference]
---
`jplrepo` is a tribute mod in the unscience repository that cycles three phrases as coloured pseudo-menus on KSA's top bar, sliding word by word between phrases. It is a development reference (the supermod does not load it) and shows a technique worth copying when you want to restyle or decorate the bar, not just add a menu: capture a local layout value from inside a game method by injecting a call at a specific call site, then use it from the game's public modding hook. Demonstrated by [Animated menu-bar text via a call-site transpiler and a hook prefix](../../../../mods/unscience/features/jplrepo-menu-bar-animation.md). Prerequisites: Harmony transpilers (see [Swapping one call inside a game method with a transpiler](../../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)).

## Background: how the game does it

`DrawMenuBar` positions the version string by jumping the cursor: `SetCursorPosY(cursorPosY)` then `SetCursorPosX(...)`, after the hook call ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4010)[^ksa-program]). In the whole method, the first and only `SetCursorPosY` call is at L4008 (the earlier `GetCursorPosY` at L3489 only reads). The hook is the empty `DrawProgramMenusHook` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018-L4020)[^ksa-program]); all menus the game draws before it advance the ImGui cursor to the right of the last stock menu.

## Lifecycle and timing

Both patches run inside `Program.DrawMenuBar` in the UI phase (`frame:draw-ui`), from `OnDrawUiThreadSafe` while the main viewport is hovered (and from hovered secondary viewports). The order inside the method in build 2026.10.10.5554 is: stock menus, `DrawProgramMenusHook()` (L4005), then the version-string block with `SetCursorPosY` (L4008). So the prefix runs before the snapshot call in the same frame: it consumes the snapshot taken on the previous frame. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Transpile: inject a snapshot call before the first `SetCursorPosY`.** Insert a void static call before the call instruction; it is stack neutral because the argument is already on the stack, so no other IL changes. Log a warning (not an exception) if the anchor is missing:

```csharp
// jplrepo/Patcher.cs L54-L82
static IEnumerable<CodeInstruction> DrawMenuBar_Transpiler(IEnumerable<CodeInstruction> instructions)
{
    // Inject SaveMenuCursorPos() right before SetCursorPosY — the first call in the
    // version-string positioning block — to snapshot cursor X/Y while it still
    // reflects the natural left-to-right menu flow.
    var setCursorPosY = AccessTools.Method(typeof(ImGui), "SetCursorPosY");
    bool injected = false;
    foreach (var instr in instructions)
    {
        if (!injected && instr.Calls(setCursorPosY))
        {
            yield return new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(Patcher), nameof(SaveMenuCursorPos)));
            injected = true;
        }
        yield return instr;
    }
    if (!injected)
        Console.WriteLine("jplrepo: WARNING - could not find SetCursorPosY in DrawMenuBar IL");
}

static void SaveMenuCursorPos()
{
    _savedCursorX = ImGui.GetCursorPosX();
    _savedCursorY = ImGui.GetCursorPosY();
}

static void DrawProgramMenusHook_Prefix()
{
```

2. **Restore the cursor in a prefix on the hook.** The prefix restores the saved position and draws the animation:

```csharp
// jplrepo/Patcher.cs L83-L88
    // Restore the cursor to where it was after the last built-in menu,
    // before SetCursorPosX pushed it to the far right for the version string.
    ImGui.SetCursorPosX(_savedCursorX);
    ImGui.SetCursorPosY(_savedCursorY);
    MenuBarAnimation.Draw();
}
```

3. **Draw words as `BeginMenu` items over filled backgrounds.** A word is a `BeginMenu` item so it spaces and hovers like a real menu; style colours `Header`, `HeaderHovered`, `HeaderActive` and `Text` are pushed to the phrase colour and a filled rectangle is drawn behind the text on the window draw list:

```csharp
// jplrepo/MenuBarAnimation.cs L87-L116
private static void DrawWords(string[] words, int from, int to, int seqIndex)
{
    uint bgColor   = ImGui.GetColorU32(Colors[seqIndex]);
    uint textColor = ImGui.GetColorU32(TextColors[seqIndex]);

    var  drawList  = ImGui.GetWindowDrawList();
    var  style     = ImGui.GetStyle();
    float padX     = style.FramePadding.X;
    float itemH    = ImGui.GetTextLineHeightWithSpacing();

    // Header/Hovered/Active all match so the bg stays consistent when opened/hovered.
    ImGui.PushStyleColor(ImGuiCol.Header,        bgColor);
    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, bgColor);
    ImGui.PushStyleColor(ImGuiCol.HeaderActive,  bgColor);
    ImGui.PushStyleColor(ImGuiCol.Text,          textColor);

    for (int i = from; i <= to; i++)
    {
        // Draw the background rect before BeginMenu so it sits behind the text.
        float2 pos   = ImGui.GetCursorScreenPos();
        float  itemW = ImGui.CalcTextSize(words[i]).X + padX * 2f;
        float2 pMax  = pos + new float2(itemW, itemH);
        drawList.AddRectFilled(in pos, in pMax, bgColor);

        if (ImGui.BeginMenu(words[i]))
            ImGui.EndMenu();
    }

    ImGui.PopStyleColor(4);
}
```

4. **Drive the state with wall-clock time.** `MenuBarAnimation` holds each phrase for 2 s and then shifts one word every 50 ms, independent of frame rate.

5. **Apply and remove with `[StarMapAllModsLoaded]` / unload.** The host only patches at all-mods-loaded and unpatches at unload ([jplrepo/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Mod.cs)).

## Complete example

| File | Role |
|---|---|
| [jplrepo/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Patcher.cs) | Transpiler, cursor snapshot, hook prefix |
| [jplrepo/MenuBarAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/MenuBarAnimation.cs) | Hold/slide state machine and word drawing |
| [jplrepo/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Mod.cs) | Standalone StarMap host |

## Variations and gotchas

- **Snapshot timing (unverified in game).** The code comment assumes the snapshot is taken "after the last built-in menu", before the hook. In this build the hook call precedes the version-string `SetCursorPosY`, so the snapshot is taken after the hook, including whatever this prefix and other mods' hooks drew, and is applied on the next frame. Because `BeginMenu` advances the cursor, the saved X may drift right by the drawn width. A more robust variant snapshots in the prefix itself (the cursor at hook entry is already after the built-in menus) and needs no transpiler.
- **Single overload assumption.** `AccessTools.Method(typeof(Program), "DrawMenuBar")` relies on one overload; an ambiguity or rename breaks patching.
- **Missing anchor.** If the IL anchor is missing the transpiler only logs; the prefix then restores (0, 0).
- **Only for decoration.** If you only need an entry, use [approach 1](add-items-to-the-game-menu-bar-1.md).
- **Version.** Depends on the exact shape of `DrawMenuBar` in 2026.10.10.5554.

## See also

- [Overview of the menu bar](overview.md)
- [Approach 1: Harmony postfix on DrawProgramMenusHook](add-items-to-the-game-menu-bar-1.md)
- [Swapping one call inside a game method with a transpiler](../../../../howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)
- Mod feature: [Animated menu-bar text via a call-site transpiler and a hook prefix](../../../../mods/unscience/features/jplrepo-menu-bar-animation.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
