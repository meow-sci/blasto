---
type: How-To
title: Check that every patch class still applies to a new game build with a throwaway console app
description: Load the built mod DLL and a new set of game DLLs offline and run CreateClassProcessor on every patch class the way the mod's Patcher does, to catch target and parameter-name drift the compiler cannot
category: testing-and-tooling
group: offline-tests
topic: test-harmony-patches-without-the-game
approach: 2
difficulty: advanced
prerequisites: [harmony, dotnet-sdk]
demonstrated_by: [mods/purrtty/features/ksa-upgrade-coupling-review-skill]
techniques: [harmony-postfix, assembly-load-context, patch-apply-check]
hooks: [harmony-postfix KSA.Program.DrawProgramMenusHook]
lifecycle: [event:game-update, startup:all-mods-loaded]
game_types: [KSA.Program]
tags: [testing, harmony, patch-apply, game-update, offline, assemblyloadcontext, arm64]
---

Outcome: after a game update, a few seconds of offline work tell you which Harmony patches would fail to apply, without starting the game. The check is the same loop the mod runs at startup, run over the new game DLLs.
Demonstrated by purrTTY, whose upgrade skill describes the throwaway app and whose production `Patcher` is the loop it copies ([purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L38-L71)). The throwaway app itself is described in the skill and is not checked in. Prerequisites: Harmony and the same `Lib.Harmony` version as the mod.

## Background: how the game does it

A Harmony patch fails at load, not at compile: a renamed or retyped target throws (or is logged) when `PatchProcessor` resolves it, and a compile against the new DLLs does not notice a patch attribute that names a method by string. purrTTY classifies its patches as required (input gating, typing guard) or optional (menu fallback, console capture, in-world draw injection) and applies each one independently so one drifted target cannot abort the mod.
One of those targets is `Program.DrawProgramMenusHook`, a public empty hook the game calls at the end of its menu bar ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018)); a postfix on it replaced an older fragile transpiler.

## Lifecycle and timing

Offline. In the game the same loop runs at mod load (`startup:all-mods-loaded`, from the mod's patch entry point); the offline app runs it at upgrade time (`event:game-update`). Coupling classes decide which checks matter:

| Coupling | Fails at | Caught by |
|---|---|---|
| Direct member reference | compile | `dotnet build` against the new `dll/` |
| Harmony patch target | load, logged not thrown | the offline patch-apply app, or the mod log |
| Reflection by name | runtime, silently | diff only |
| Behavioural change | runtime, silently wrong | diff plus reasoning |

See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for where each patched method runs.

## Steps

1. **Keep patch application in one loop that applies each class independently.** The production loop is the shape the offline app copies: required patches first, optional ones after, each wrapped so a failure is logged with its severity:

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

2. **Create a throwaway console project** with the same `Lib.Harmony` version as the mod's csproj, referencing nothing at compile time except Harmony. It takes two arguments: the folder holding the built mod DLL and the folder of the new game DLLs (`<current-ksa>/dll`).

3. **Register an `AssemblyLoadContext.Default.Resolving` probe before any method that mentions game types is JIT-compiled.** Keep the body that touches KSA types in a separate `[MethodImpl(MethodImplOptions.NoInlining)]` method, or the JIT resolves the types while compiling `Main`, before the probe exists:

   ```csharp
   // adapted from purrTTY.GameMod/Patcher.cs L56-L71 and .claude/skills/upgrade-ksa/SKILL.md L123-L134
   using System.Reflection;
   using System.Runtime.CompilerServices;
   using System.Runtime.Loader;
   using HarmonyLib;

   string modDir = args[0], ksaDir = args[1];
   AssemblyLoadContext.Default.Resolving += (context, name) =>
   {
       foreach (string dir in new[] { modDir, ksaDir })
       {
           string path = Path.Combine(dir, name.Name + ".dll");
           if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
       }
       return null;
   };
   return Check(Path.Combine(modDir, "purrTTY.GameMod.dll"));

   [MethodImpl(MethodImplOptions.NoInlining)]
   static int Check(string modDll)
   {
       Assembly mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modDll);
       var harmony = new Harmony("offline-patch-check");
       int failures = 0;
       foreach (Type type in mod.GetTypes().Where(t => t.GetCustomAttributes<HarmonyPatch>().Any()))
       {
           try { harmony.CreateClassProcessor(type).Patch(); }
           catch (Exception ex) { failures++; Console.WriteLine($"FAIL {type.Name}: {ex.Message}"); }
       }
       foreach (MethodBase target in harmony.GetPatchedMethods())
           Console.WriteLine($"patched {target.DeclaringType?.FullName}.{target.Name}");
       return failures == 0 ? 0 : 1;
   }
   ```

4. **Report targets with the patch-info API, not the return value.** `Patch()` returns the generated replacement methods (`DeclaringType == null`); use `Harmony.GetPatchInfo` or `GetPatchedMethods` to list what was actually patched.

5. **Exercise a skipping prefix if you need to.** The skill notes that a patched private method can be invoked on an instance from `RuntimeHelpers.GetUninitializedObject` to test a prefix that skips the original; a pass-through faults in the uninitialised original, which is itself the signal.

6. **On an arm64 host, patch a scratch copy of `KSA.dll`.** `KSA.dll` is IL-only but flagged x64 (PE32+, no ReadyToRun), so copy the DLL folder to scratch and set the copy's COFF `Machine` field to `0xAA64`; never touch the snapshot itself.

7. **Wire the check into the upgrade procedure.** Run it first against the previous baseline (all green) and then against the new build; the failures are the patches to fix. The surrounding review (coupling catalog, severity per touchpoint) is in [Upgrade a mod to a new KSA build](../../game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs) | Production loop: required and optional patch classes, per-patch try/catch |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md#L60-L67) | Coupling classes and what catches each |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md#L123-L134) | Description of the offline patch-apply app and its gotchas |

## Variations and gotchas

- **Applying is not proving.** A patch that applies can still be wrong: this check catches target and parameter-name drift only. For a transpiler, run [approach 3](test-harmony-patches-without-the-game-3.md) against the new method's IL.
- **The app is not in the repository.** purrTTY keeps the procedure as a skill; the sample above is adapted from the description and from `Patcher`, not a verbatim file.
- **Optional versus required.** Decide severity per patch up front (required patches degrade core function; optional ones leave the mod usable) so the report can be read at a glance.
- **Game updates.** This is the update-time tool; keep the baseline the last validated build, not whatever the previous folder holds.

## See also

- [Overview](overview.md); [approach 1](test-harmony-patches-without-the-game-1.md), [approach 3](test-harmony-patches-without-the-game-3.md)
- [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md), [Isolate patch failures](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Track the game API your mod uses](../../game-updates/track-the-game-api-your-mod-uses/overview.md)
- Mod feature: [purrTTY upgrade coupling review skill](../../../../mods/purrtty/features/ksa-upgrade-coupling-review-skill.md)
