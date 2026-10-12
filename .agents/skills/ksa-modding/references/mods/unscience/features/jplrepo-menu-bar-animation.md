---
type: Mod Feature
title: Animated menu-bar text via a call-site transpiler and a hook prefix
description: A transpiler on Program.DrawMenuBar injects a cursor snapshot just before the version-string SetCursorPosY call, and a prefix on DrawProgramMenusHook restores that cursor and draws colored, sliding menu-bar words
mod: unscience
submod: jplrepo
category: ui
techniques: [harmony-transpiler, harmony-prefix, imgui-menu, imgui-drawlist]
hooks: [harmony-transpiler KSA.Program.DrawMenuBar, harmony-prefix KSA.Program.DrawProgramMenusHook]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.KSAColor]
tags: [menu-bar, imgui, transpiler, draw-list, animation]
---

`jplrepo` is a tiny tribute mod that cycles three "cheer" phrases ("JPLRepo! he's our man, ...") as
colored pseudo-menus on KSA's top menu bar, sliding word by word between phrases. It is a
development reference, not loaded by the supermod, but it shows a technique worth copying: capture
a local layout value from inside a game method by injecting a call at a specific call site, then use
it from the game's public modding hook.[^jplrepo-patcher][^full-scope]

Mechanism in one line: a transpiler inserts `call Patcher.SaveMenuCursorPos()` immediately before the
first `ImGui.SetCursorPosY` call in `Program.DrawMenuBar`, and a prefix on
`Program.DrawProgramMenusHook` sets the ImGui cursor to the saved position and draws the words with
`BeginMenu` items over `AddRectFilled` backgrounds.

## How it works

- `DrawMenuBar` right-aligns the build version string by jumping the cursor with
  `SetCursorPosY(cursorPosY)` and `SetCursorPosX(windowWidth - textWidth - 10)`. The transpiler walks the
  IL, finds the first instruction that `Calls(AccessTools.Method(typeof(ImGui), "SetCursorPosY"))`, and
  yields a parameterless static call before it. Inserting a `void()` call before the call instruction is
  stack-neutral (the `cursorPosY` argument is already on the stack), so no other IL changes.
- `SaveMenuCursorPos` reads `ImGui.GetCursorPosX/Y` into static fields.
- The `DrawProgramMenusHook` prefix restores those coordinates and calls `MenuBarAnimation.Draw()`.
- `MenuBarAnimation` keeps a wall-clock state machine (hold 2 s, then shift one word every 50 ms).
  Each word is a `BeginMenu` item (so it looks and spaces like a real menu) with `Header*` style colors
  pushed to the phrase color and a filled rectangle drawn behind it on the window draw list. Colors come
  from `KSAColor.Xkcd` presets (`RadioactiveGreen`, `Custard`, `ReddishPink`).[^jplrepo-animation]
- The host (`jplrepo/Mod.cs`) only patches at `[StarMapAllModsLoaded]` and unpatches at unload; its
  GUI attributes are empty.[^jplrepo-mod]

## Lifecycle and timing

Both patches run inside `Program.DrawMenuBar`, which the game calls during the UI phase
(`frame:draw-ui`) from `OnDrawUiThreadSafe` while the main viewport is hovered (and from hovered
secondary viewport windows). In build 5554 the order inside `DrawMenuBar` is: built-in menus,
`DrawProgramMenusHook()` at L4005, then the version-string block with `SetCursorPosY(cursorPosY)` at
L4008.[^ksa-program] So the prefix draws first, and the snapshot is taken afterwards, at the version
string, and is consumed by the prefix on the next frame.

## Key code

| Path | Role |
|---|---|
| [jplrepo/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Patcher.cs) | Transpiler, cursor snapshot, hook prefix, HotkeyGuard |
| [jplrepo/MenuBarAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/MenuBarAnimation.cs) | Hold/slide state machine and word drawing |
| [jplrepo/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Mod.cs) | Standalone StarMap host |

## Snippets

```csharp
// jplrepo/Patcher.cs L54-L88
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
        // Restore the cursor to where it was after the last built-in menu,
        // before SetCursorPosX pushed it to the far right for the version string.
        ImGui.SetCursorPosX(_savedCursorX);
        ImGui.SetCursorPosY(_savedCursorY);
        MenuBarAnimation.Draw();
    }
```

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.DrawMenuBar(IGameViewport, int)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455) | Transpiler target (looked up by name only) |
| `Program.DrawProgramMenusHook()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020) | Prefix target; empty modding hook |
| `KSAColor.Xkcd.*` | [current/decomp/KSA/KSAColor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KSAColor.cs) | Named color presets passed to `ImGui.GetColorU32` |

## Pitfalls and version notes

- Snapshot timing (unverified in game): the code comment assumes the snapshot is taken "after the last
  built-in menu" and before the hook runs. In builds 5348 through 5554 the hook call precedes the
  version-string `SetCursorPosY` (5554: L4005 then L4008), so the snapshot is taken after the hook,
  including whatever this prefix and any other mod's hook patches drew, and is applied on the next frame.
  Because `BeginMenu` advances the cursor, the saved X may move right by the drawn width every frame. A
  robust variant would snapshot in the prefix itself (the cursor at hook entry is already after the
  built-in menus) or inject before the hook call.
- `AccessTools.Method(typeof(Program), "DrawMenuBar")` relies on a single overload; ambiguity or a rename
  breaks patching (the catch logs it).
- If the IL anchor is missing the transpiler only logs a warning; the prefix then restores (0, 0).
- The supermod does not load this project ([scope/FULL_SCOPE.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/FULL_SCOPE.md) lists it as a development reference).

## See also

- [Unscience menu-bar entry](./unscience-menu-bar-entry.md) (postfix on the same hook)
- [XKCD color catalog](./lib-xkcd-color-catalog.md)
- [Sub-mod: jplrepo](../submods/jplrepo.md)
- [Mod overview](../overview.md)
- [Snapshot state before a stock call, restore it in a paired prefix](../../../howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-3.md) - how-to article demonstrating this feature
- [Decorate the menu bar itself with a transpiler snapshot and a hook prefix](../../../howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-3.md) - how-to article demonstrating this feature

[^jplrepo-patcher]: jplrepo/Patcher.cs
[^full-scope]: scope/FULL_SCOPE.md
[^jplrepo-animation]: jplrepo/MenuBarAnimation.cs
[^jplrepo-mod]: jplrepo/Mod.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
