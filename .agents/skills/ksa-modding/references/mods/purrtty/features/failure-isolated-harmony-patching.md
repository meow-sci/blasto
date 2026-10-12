---
type: Mod Feature
title: Failure-isolated Harmony patching with reload-safe statics
description: Apply each Harmony patch class on its own with CreateClassProcessor inside a try/catch and classify patches as required or optional so a drifted game target never takes the mod down, plus a defensive lazy Harmony instance and static-state reset on unload
mod: purrtty
category: patching
techniques: [harmony-prefix, harmony-postfix, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Program, KSA.GameSettings, KSA.SuperMeshRenderSystem, Brutal.ImGuiApi.Abstractions.ConsoleWindow]
tags: [harmony, robustness, game-updates, starmap, reload]
---

purrTTY patches six game methods. Any of them can be renamed or reshaped by a KSA update, and
Harmony's `PatchAll` stops at the first failure, which once aborted the whole mod initialization.
`Patcher` instead applies each patch class separately, logs failures with a severity that says
what the player loses, and always lets the terminal start. Its unload path also resets static
patch state for a StarMap reload that keeps the assembly loaded; the pinned StarMap has no such
reload, so that part is defensive.

## How it works

- Two arrays of patch classes: **required** (`Patch01` keyboard gate on `Program.OnKey`,
  `Patch03_HotkeyGuard` on `GameSettings.OnKeyAll`, `Patch04_MouseBindingGate` on
  `Program.DispatchKeyEvent`) and **optional** (`Patch02` menu fallback on
  `Program.DrawProgramMenusHook`, `ConsoleWindowPrintPatch`, `RenderTranslucencyPassPatch`).[^patcher]
- `ApplyPatch` calls `harmony.CreateClassProcessor(type).Patch()` inside a try/catch and logs
  "REQUIRED ... terminal input gating may be degraded" or "optional ... the mod remains usable".
- Separate classes per target: a drift in the private `DispatchKeyEvent` can only disable
  `Patch04`, never the `OnKey` gate.
- `m_harmony ??= new Harmony("purrTTY")`: `unload()` nulls the instance after `UnpatchAll("purrTTY")`,
  so a later `patch()` would recreate it instead of silently patching nothing through a null reference.
- `unload()` also calls `Reset()` on `Patch02` (cached ModMenu-presence probe), `Patch01` (held-key
  set) and `Patch04` (swallowed-button set), because a StarMap reload without an ALC unload would keep
  static state (gotcha 21).[^gotchas] StarMap 4dc60f8 never reloads a mod in-process: mods load once
  in `StarMapCore.Init`, and its "new mods" restart starts a new process and calls
  `Environment.Exit(0)`.[^starmap-library-patches] Both measures are therefore defensive against a
  future loader, not something the pinned StarMap exercises.
- `TerminalMod.OnFullyLoaded` calls `Patcher.patch()` first and then initializes the terminal, the
  in-world subsystem and layouts, each in its own try/catch; `OnAfterUi` wraps all per-frame work in
  a try/catch that logs and continues.[^terminal-mod]

## Lifecycle and timing

| Hook | Action |
|---|---|
| `[StarMapAllModsLoaded]` (Harmony postfix on `ModLibrary.LoadAll`) | `Patcher.patch()`; game types and all mods exist, the first frame has not run[^starmap-library-patches] |
| `[StarMapUnload]` (from StarMap `ModLoader.Dispose`, at game exit) | `Patcher.unload()`: unpatch, null the instance, reset statics. `StarMapCore.DeInit` calls `UnpatchAll()` with no owner id before `ModLoader.Dispose`, so every Harmony patch in the process, purrTTY's included, is already gone and `UnpatchAll("purrTTY")` removes nothing[^starmap-core][^starmap-loader] |

Patching at all-mods-loaded rather than immediate-load avoids touching game types before the game
has finished loading its content. The input, menu and render targets are first reached by the frame
loop, which starts after this point. `ConsoleWindow.Print` is not: `Program`'s constructor registers
the console window as a log provider before any mod hook, so log lines printed during loading pass
through it unpatched. That loses nothing, because the capture postfix only forwards output while a
Game Console command is executing.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L13-L86) | Classification, per-class application, unload and resets |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L130-L156) | Patching first, then isolated initializers |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md) | Checklist that lists every patch target to re-verify per KSA build |

## Snippets

```csharp
// purrTTY.GameMod/Patcher.cs L38-L71
  public static void patch()
  {
    // Recreate the instance if a previous unload() nulled it: a StarMap reload
    // without an ALC unload reuses these static types, and `null?.Patch…` would
    // make patch() a silent no-op (no input gating, no menu, no capture).
    var harmony = m_harmony ??= new Harmony("purrTTY");

    foreach (var type in s_requiredPatches)
    {
      ApplyPatch(harmony, type, required: true);
    }

    foreach (var type in s_optionalPatches)
    {
      ApplyPatch(harmony, type, required: false);
    }
  }

  private static void ApplyPatch(Harmony harmony, Type type, bool required)
  {
    try
    {
      harmony.CreateClassProcessor(type).Patch();
    }
    catch (Exception ex)
    {
      string severity = required ? "REQUIRED" : "optional";
      string consequence = required
        ? "terminal input gating may be degraded; the terminal will still initialize"
        : "the mod remains usable without it";
      ModLog.Log.Error(
        $"purrTTY: {severity} Harmony patch '{type.Name}' failed to apply ({consequence}): {ex.Message}");
    }
  }
```

```csharp
// purrTTY.GameMod/Patcher.cs L73-L85
  public static void unload()
  {
    m_harmony?.UnpatchAll("purrTTY");
    m_harmony = null;

    // Statics survive a StarMap reload without an ALC unload (same reason
    // m_harmony is recreated lazily above), so cached environment probes must
    // be re-evaluated by the next patch() — the mod set may have changed — and
    // the held-key model must not carry stale entries across a reload.
    Patch02.Reset();
    Patch01.Reset();
    Patch04_MouseBindingGate.Reset();
  }
```

## Game APIs touched

The patch targets, all present in build 2026.10.10.5554: `Program.OnKey`,
`Program.DispatchKeyEvent` (private), `Program.DrawProgramMenusHook`, `GameSettings.OnKeyAll`,
`ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8, int)`, `SuperMeshRenderSystem.RenderTranslucencyPass`.
Their behavior is covered in the feature files linked below.

## Pitfalls and version notes

- `CreateClassProcessor(type).Patch()` throws for a missing target; catch per class, not around a loop.
- Classify honestly: if a "required" patch fails, say what breaks for the player in the log line.
- Do not rely on `StarMapUnload` to remove patches: at the pinned StarMap they are already removed,
  and the device and game objects are torn down by then.
- Statics would survive a loader reload that keeps the assembly loaded; resetting cached probes,
  held-key sets and the Harmony instance on unload is cheap insurance, but StarMap 4dc60f8 does not
  reload mods.
- In 5554, `RenderTranslucencyPass` takes `(IViewport viewport, CommandBuffer commandBuffer, bool
  useCustomRenderPass)`; purrTTY's postfix declares the parameters in a different order, which works
  because Harmony injects by parameter name.

## See also

- [Gate game input while a terminal is focused](./gate-game-input-while-terminal-focused.md)
- [Game menu with ModMenu fallback](./game-menu-with-modmenu-fallback.md)
- [Game console shell](./game-console-shell.md)
- [Draw after the translucency pass](./draw-after-translucency-pass-postfix.md)
- [KSA upgrade coupling review skill](./ksa-upgrade-coupling-review-skill.md)
- [purrTTY overview](../overview.md)
- [CreateClassProcessor per patch class, required versus optional](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md) - how-to article demonstrating this feature

[^patcher]: purrTTY.GameMod/Patcher.cs
[^gotchas]: purrTTY docs/gotchas.md
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^starmap-library-patches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-loader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
