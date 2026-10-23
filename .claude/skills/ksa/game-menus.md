# Adding Top-Level Menus to the KSA Game Title Bar

## How the Game Menu Bar Works

The game draws its menu bar in `Program.DrawMenuBar(IGameViewport viewport, int windowWidth)` (an
instance method). It creates a borderless ImGui window with `ImGuiWindowFlags.MenuBar` and calls
`ImGui.BeginMenuBar()` / `ImGui.EndMenuBar()` inside it. The built-in menus rendered inside that
block are, in order:

1. **File** — save/load, exit, settings
2. **Editor** (when in editor mode) *or* **Universe** (in flight) — simulation controls
3. **View** — cameras, overlays, orbit lines, debug tools

Right after the last built-in menu — and still **inside** the `BeginMenuBar()` scope — the game
calls an empty public extension point:

```csharp
public void DrawProgramMenusHook()
{
}
```

A plain Harmony **postfix on `Program.DrawProgramMenusHook`** is therefore the way to add a
top-level menu. No IL rewriting needed; this is what purrTTY ships (`Patch02` in
`purrTTY.GameMod/Patcher.cs`).

## Recommended Pattern: Postfix on `DrawProgramMenusHook`

```csharp
using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;

[HarmonyPatch(typeof(KSA.Program), nameof(KSA.Program.DrawProgramMenusHook))]
static class MyMenuPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (ImGui.BeginMenu("My Mod"))
        {
            // REQUIRED: keeps the (possibly auto-hidden) menu bar shown and
            // suppresses game hotkeys/camera while the menu is open.
            // KSA 5402: MenuBarInUse is read-only on IGameViewport; the setter is on
            // the public IGameViewportLifecycle interface (what DrawMenuBar itself calls).
            ((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true);

            if (ImGui.MenuItem("Do Something", default, _enabled))
                _enabled = !_enabled;

            if (ImGui.BeginMenu("Submenu"))
            {
                ImGui.Text("Nested content");
                ImGui.EndMenu();
            }

            ImGui.EndMenu();
        }
    }

    private static bool _enabled = false;
}
```

Notes:

- The hook is called once per frame for the **MainViewport** menu bar, right after the View menu
  and before the right-aligned version string + `EndMenuBar()`.
- Call `SetMenuBarInUse(true)` **inside** the `BeginMenu` block (only while the menu is open).
  Omitting it lets the game steal keyboard/mouse input from your menu. (Pre-5402 builds exposed
  a settable `Viewport.MenuBarInUse` property instead.)
- Apply patches in `[StarMapAllModsLoaded]`, not the mod constructor, and apply each patch class
  independently (`harmony.CreateClassProcessor(type).Patch()` with try/catch) so one drifted
  target cannot take down the whole mod.

```csharp
[StarMapMod]
public class Mod
{
    [StarMapAllModsLoaded]
    public void OnFullyLoaded() => Patcher.Patch();

    [StarMapUnload]
    public void Unload() => Patcher.Unload();
}
```

## Legacy: Harmony Transpiler on `DrawMenuBar`

Before `DrawProgramMenusHook` existed, the only injection point was an IL transpiler on
`Program.DrawMenuBar` that located the View menu's `ImGui.EndMenu()` call and inserted
instructions after it. **Do not use this anymore**: the IL layout around the insertion point has
changed across game builds (the version-string instructions are now gated behind an
`if (viewport == MainViewport)` block, so fixed instruction offsets are unreliable), and the
opcode pattern-matching breaks silently on every reshuffle. purrTTY replaced its transpiler with
the postfix above for exactly this reason.

If you must inject elsewhere in `DrawMenuBar`, note it is an **instance** method, so `Ldarg_1`
is the `IGameViewport` parameter.

## ImGui Calls Available Inside the Menu

Once inside the `BeginMenu("...")` block you can use all standard ImGui menu calls:

```csharp
ImGui.MenuItem("Label")                        // simple clickable item, returns bool
ImGui.MenuItem("Label", default, isSelected)   // with checkmark toggle
ImGui.MenuItem("Label", shortcutStr, selected, enabled)  // full overload
ImGui.BeginMenu("Submenu") / ImGui.EndMenu()   // nested submenu
ImGui.Separator()                              // horizontal divider
ImGui.Text("Info text")                        // non-interactive text
ImGui.TextColored(color, "Colored text")
```
