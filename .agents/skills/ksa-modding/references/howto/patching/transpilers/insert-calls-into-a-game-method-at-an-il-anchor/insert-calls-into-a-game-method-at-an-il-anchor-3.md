---
type: How-To
title: Snapshot state before a stock call, restore it in a paired prefix
description: Capture a local layout value from inside Program.DrawMenuBar by injecting a stack-neutral call before the first SetCursorPosY, then use it from a prefix on the public DrawProgramMenusHook, with an honest look at the timing of the snapshot
category: patching
group: transpilers
topic: insert-calls-into-a-game-method-at-an-il-anchor
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/jplrepo-menu-bar-animation]
techniques: [harmony-transpiler, harmony-prefix, il-insert]
hooks: [harmony-transpiler KSA.Program.DrawMenuBar, harmony-prefix KSA.Program.DrawProgramMenusHook]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program]
tags: [harmony, transpiler, il, insert, snapshot, imgui, menu-bar, prefix]
---

The jplrepo development mod draws animated pseudo-menus on KSA's top menu bar. To place them it needs a layout value that exists only as ImGui state at one moment inside
`Program.DrawMenuBar`: the cursor position after the built-in menus. The mod splits the job between IL insertion and an ordinary prefix. A transpiler inserts a parameterless static call
`SaveMenuCursorPos()` before the first `ImGui.SetCursorPosY` call; a prefix on the public hook `Program.DrawProgramMenusHook` restores the saved cursor and draws. This article shows the pattern
and what the IL order means for the snapshot's freshness.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience's jplrepo in [Animated menu-bar text via a call-site transpiler and a hook prefix](../../../../mods/unscience/features/jplrepo-menu-bar-animation.md).

## Background: how the game does it

`DrawMenuBar` draws the built-in menus, calls the empty public extension point `DrawProgramMenusHook()`, then right-aligns the version string by jumping the cursor:

```csharp
// current/decomp/KSA/Program.cs L4005-L4011
DrawProgramMenusHook();
if (viewport.IsMain())
{
	ImGui.SetCursorPosY(cursorPosY);
	ImGui.SetCursorPosX((float)windowWidth - ImGui.CalcTextSize(VersionInfo.Current.VersionString).X - 10f);
	ImGui.TextColored(in Color.Gray.AsFloat4, VersionInfo.Current.VersionString);
}
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4011)[^ksa-program]

The IL: `DrawProgramMenusHook` is called at `IL_19d6`; the version-string block starts at `IL_19e9` with `ldloc.s 13` (the `cursorPosY` argument) and the `SetCursorPosY` call at `IL_19eb`:

```text
// KSA.dll IL via ilspycmd -il: KSA.Program::DrawMenuBar (parameter lists shortened)
IL_19d5: ldarg.0
IL_19d6: call instance void KSA.Program::DrawProgramMenusHook(...)
IL_19db: nop
IL_19dc: ldarg.1
IL_19dd: call bool KSA.ViewportEx::IsMain(...)
IL_19e2: stloc.s 137
IL_19e4: ldloc.s 137
IL_19e6: brfalse.s IL_1a3f

IL_19e8: nop
IL_19e9: ldloc.s 13
IL_19eb: call void [Brutal.ImGui]Brutal.ImGuiApi.ImGui::SetCursorPosY(...)
IL_19f0: nop
```

Source: [current/dll/KSA.dll](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/dll/KSA.dll)[^ksa-dll]

## Lifecycle and timing

Both patches run inside `Program.DrawMenuBar`, which the game calls in the UI phase (`frame:draw-ui`) from `OnDrawUiThreadSafe` while the main viewport is hovered ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3188)). The order in
the method is: built-in menus, `DrawProgramMenusHook()` (IL_19d6), then the version-string block with `SetCursorPosY` (IL_19eb). Consequently **the prefix on the hook runs before the snapshot is taken**, so the
prefix restores the position saved on the *previous* frame, and the snapshot is taken after the hook (including whatever the prefix and any other mod's hook patch drew). Because `BeginMenu` advances
the cursor, the saved X can move right by the drawn width every frame (the mod's own notes mark this as unverified in game). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick an anchor that is stack-neutral to insert before.** `SetCursorPosY(float)` has its argument already on the stack; inserting a `void()` call before it changes nothing else. The transpiler
   finds the first call and yields the new instruction before it, logging a warning if the anchor is absent:

```csharp
// jplrepo/Patcher.cs L54-L79
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
```

Source: [jplrepo/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Patcher.cs#L54-L79)[^unscience-patcher]

2. **Pair it with a prefix on the public hook.** The hook is the game's own extension point, so the prefix is robust; it restores the cursor and draws:

```csharp
// jplrepo/Patcher.cs L81-L88
static void DrawProgramMenusHook_Prefix()
{
    // Restore the cursor to where it was after the last built-in menu,
    // before SetCursorPosX pushed it to the far right for the version string.
    ImGui.SetCursorPosX(_savedCursorX);
    ImGui.SetCursorPosY(_savedCursorY);
    MenuBarAnimation.Draw();
}
```

Source: [jplrepo/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Patcher.cs#L81-L88)

3. **Apply both from one `Patch` and remove by id.** The transpiler targets `DrawMenuBar` by name (a single overload), the prefix the public hook:

```csharp
// jplrepo/Patcher.cs L19-L52
public static void Patch()
{
    try
    {
        if (_harmony == null) return;
        // Transpiler snapshots cursor pos before SetCursorPosX blasts it to the right edge.
        _harmony.Patch(
            AccessTools.Method(typeof(Program), "DrawMenuBar"),
            transpiler: new HarmonyMethod(typeof(Patcher), nameof(DrawMenuBar_Transpiler)));
        // DrawProgramMenusHook prefix restores cursor and draws our menus.
        _harmony.Patch(
            AccessTools.Method(typeof(Program), nameof(Program.DrawProgramMenusHook)),
            prefix: new HarmonyMethod(typeof(Patcher), nameof(DrawProgramMenusHook_Prefix)));
        HotkeyGuard.Patch(_harmony);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"jplrepo: Error applying patches: {ex.Message}");
    }
}

public static void Unload()
{
    try
    {
        if (_harmony != null) HotkeyGuard.Unpatch(_harmony);
        _harmony?.UnpatchAll("jplrepo");
        _harmony = null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"jplrepo: Error removing patches: {ex.Message}");
    }
}
```

Source: [jplrepo/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Patcher.cs#L19-L52)

4. **Know the weakness and the variant.** The timing issue above means the snapshot is one frame old and taken after the hook. A more robust variant snapshots in the prefix itself (the cursor at hook
   entry is already after the built-in menus) or injects the call before the hook call instead of before `SetCursorPosY`.

## Complete example

| File | Role |
|---|---|
| [jplrepo/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/jplrepo/Patcher.cs) | Transpiler, snapshot, prefix, install and removal |

## Variations and gotchas

- **Inserting a `void()` call before a call is stack-neutral;** the call's arguments stay on the stack.
- **When the anchor is a branch target,** decide whether to move labels ([Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md), [Leave labels on the anchor so a jump skips the addition](insert-calls-into-a-game-method-at-an-il-anchor-2.md)). Here the `SetCursorPosY` call is inside the `IsMain()` block, reached by fall-through, so labels are not an issue.
- **`DrawMenuBar` is a public instance method with a single overload** (`IGameViewport`, `int`); `AccessTools.Method(typeof(Program), "DrawMenuBar")` relies on that.
- **If the anchor is missing the transpiler only logs;** the prefix then restores (0, 0).
- **Not loaded by the supermod.** jplrepo is a development reference.

## See also

- [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md), [Leave labels on the anchor so a jump skips the addition](insert-calls-into-a-game-method-at-an-il-anchor-2.md), [Insert before the first and after the last instance of a pass boundary](insert-calls-into-a-game-method-at-an-il-anchor-4.md)
- [Inserting calls into a game method at an IL anchor](overview.md)
- [Animated menu-bar text via a call-site transpiler and a hook prefix](../../../../mods/unscience/features/jplrepo-menu-bar-animation.md)
- Related task: add-items-to-the-game-menu-bar

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-dll]: KSA.dll (build 2026.10.10.5554), IL read with ilspycmd
[^unscience-patcher]: unscience/jplrepo/Patcher.cs
