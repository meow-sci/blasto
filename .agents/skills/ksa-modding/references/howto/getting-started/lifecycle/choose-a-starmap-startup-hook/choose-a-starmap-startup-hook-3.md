---
type: How-To
title: "StarMapAllModsLoaded: work that needs the full mod set"
description: Use the postfix on ModLibrary.LoadAll to install the bulk of your Harmony patches, check the game version, set up logging and start background warm-ups once every mod and asset bundle is loaded
category: getting-started
group: lifecycle
topic: choose-a-starmap-startup-hook
approach: 3
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/starmap/features/startup-lifecycle-hooks
  - mods/ksarmory/features/starmap-entry-and-fault-containment
  - mods/gatos/features/compile-gated-game-seam
  - mods/ksaero/features/game-version-check
techniques: [starmap-lifecycle, harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.ModLibrary.LoadAll]
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: [KSA.ModLibrary, KSA.Program, KSA.Universe, KSA.VersionInfo]
tags: [starmap, all-mods-loaded, harmony, version-check, startup, warmup]
---

`StarMapAllModsLoaded` is the default home for startup work. It fires once, as a Harmony postfix on
`ModLibrary.LoadAll`, when every mod's fonts, asset bundles and system templates are loaded and every StarMap mod
is initialized, but before the GPU upload, `AttachGameData` and the universe. KSAero, AdvancedFlightComputer,
KSArmory and gatOS all install their patches here. Use it unless you need something earlier
([StarMapBeforeMain - patch code that runs during startup](./choose-a-starmap-startup-hook-1.md), [StarMapImmediateLoad - register data before assets deserialize](./choose-a-starmap-startup-hook-2.md)).

## Background: how the game does it

StarMap dispatches the hook from a postfix on `ModLibrary.LoadAll` to every registered method, in mod
initialization order:[StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L14-L28)

```csharp
// StarMap.Core/Patches/ModLibraryPatches.cs L14-L28
[HarmonyPatch(typeof(ModLibrary))]
internal class ModLibraryPatches
{
    [HarmonyPatch(nameof(ModLibrary.LoadAll))]
    [HarmonyPostfix]
    public static void AfterLoad()
    {
        var modRegistry = StarMapCore.Instance?.Loader.ModRegistry;
        if (modRegistry is not ModRegistry registry) return;

        foreach (var (_, @object, method) in registry.Get<StarMapAllModsLoadedAttribute>())
        {
            method.Invoke(@object, []);
        }
    }
```

`LoadAll` loads fonts, asset bundles, systems and planet meshes per mod, collects console lists, runs all
`ILoader`s in parallel, then `OnDataLoaded`.[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) It has a single call site (the `Program`
constructor, L942); `AssignDefaults` (L943), viewports, `JobSystems.Initialize`, `ModLibrary.Bind` (L978),
`AttachGameData` (L985) and `Universe.LoadDefaultSystem` (L1069) all come after.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L941-L943)

## Lifecycle and timing

`startup:all-mods-loaded`, once, main thread.

| Exists | Does not exist yet |
|---|---|
| Everything from immediate load, plus mod fonts registered, asset bundles deserialized (parts, sounds, textures as CPU-side references), system templates, all `ILoader`s run, every other StarMap mod initialized | `AssignDefaults` (default textures), viewports and `Program.MainViewport`, render targets, `JobSystems`, GPU assets (`Bind`), part game data on templates (`AttachGameData`), sounds, the universe and celestials, vehicles, gauges |

Nothing in StarMap marks "the scene is ready"; mods needing `Universe`, vehicles or viewports do it lazily on the
first frame hook or patch the game themselves (the how-to topic `draw-ui-while-the-hud-is-hidden`, [StarMap lifecycle attributes](../game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)).

## Steps

1. **Declare the hook** on your `[StarMapMod]` class; public, parameterless.
2. **Check the game version first**, so a mismatch is logged right before any patch failure. KSAero's hook is the
   minimal complete example (details in [Runtime VersionInfo compare at AllModsLoaded](../check-the-game-version/check-the-game-version-1.md)):[KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L8-L36)

   ```csharp
   // KSAero/KSAero/Mod.cs L16-L36
       [StarMapAllModsLoaded]
       public void OnFullyLoaded()
       {
           string gameVersion = VersionInfo.Current.VersionString;
           if (gameVersion != TestedGameVersion)
               DefaultCategory.Log.Warning($"[KSAero] Tested against {TestedGameVersion}, current is {gameVersion}.");

           var harmony = new Harmony(HarmonyId);
           try
           {
               AirForcePatch.Apply(harmony);
               _harmony = harmony;
               DefaultCategory.Log.Info("[KSAero] Loaded. Atmospheric forces now use the body-of-revolution model.");
           }
           catch (Exception ex)
           {
               // The transpiler refuses rather than guesses, so a failure here leaves stock aero running.
               harmony.UnpatchAll(HarmonyId);
               DefaultCategory.Log.Warning($"[KSAero] Patching failed, stock aerodynamics stay in place: {ex}");
           }
       }
   ```

3. **Validate, then patch, one subsystem at a time.** AdvancedFlightComputer checks reflection targets per feature
   and applies each patch group in its own guarded call, so one failure disables one feature:[AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L30-L100)

   ```csharp
   // KSA-AdvancedFlightComputer/AdvancedFlightComputer/Mod.cs L39-L47
           bool coreReady = Validated("Core", GameReflection.ValidateCore)
               && _patches.TryApply("Core", PatchCore);

           // Its own owner, so a settings-window rename costs the mod sections and nothing else.
           if (Validated("SettingsPage", GameReflection.ValidateSettingsPage))
               _patches.TryApply("SettingsPage", ModSettingsPage.ApplyPatches);

           if (Validated("HyperbolicTargets", GameReflection.ValidateHyperbolicTargets))
               _patches.TryApply("HyperbolicTargets", HyperbolicTargets.ApplyPatches);
   ```

4. **Order the setup deliberately.** KSArmory's hook closes its pack registry first (every dependent mod's entry
   point has run by now), audits against loaded part templates, repairs saves, then installs hooks:[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L149-L215)

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L149-L160
       [StarMapAllModsLoaded]
       public void OnFullyLoaded()
       {
           // Every pack that was going to call Armoury.Register has done so by now: StarMap runs a
           // dependent mod's entry point before this hook, which is a postfix on ModLibrary.LoadAll.
           // Shutting the door here is what stops a registry growing under a crewed system.
           // Asset-only packs, found by convention in every mod's folder. A pack that ships code has
           // already called Armoury.Register from its own entry point by now; this is for the ones
           // with no assembly at all, which is most of them.
           InstalledPacks.RegisterAll();

           JitWarmup.Start();
   ```

5. **Start background warm-ups here if you have them.** KSArmory compiles its own and some engine methods on a task
   so the first shot does not hitch (`thread:background`); state the synchronization point back to the main thread
   yourself:[src/KSArmory/Ksa/JitWarmup.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/JitWarmup.cs#L26-L38)

   ```csharp
   // src/KSArmory/Ksa/JitWarmup.cs L26-L38
       public static void Start()
       {
           Task.Run(() =>
           {
               var clock = System.Diagnostics.Stopwatch.StartNew();
               int compiled = 0;

               foreach (Type type in typeof(JitWarmup).Assembly.GetTypes()) compiled += Prepare(type);
               foreach (Type type in EngineTypes) compiled += Prepare(type);

               Log.Info($"compiled {compiled} method(s) ahead of the first shot in {clock.Elapsed.TotalMilliseconds:F0} ms");
           });
       }
   ```

6. **Never let it throw.** gatOS wraps the entire init in a `try`/`catch` ("Never throws"), installs the fallback
   menu before any early return so the failure is visible, and defers the heavy part (VM boot) until later:[gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L203-L225)

   ```csharp
   // gatOS.GameMod/Mod.cs L212-L225
       [StarMapAllModsLoaded]
       public void OnFullyLoaded()
       {
           try
           {
               TryInstallGameLogging();
               _instance = this;

               // Install the fallback "gatOS" menu now (before any early return below) so the menu is
               // reachable even when ModMenu is absent and even when init fails — mirroring the way the
               // [ModMenuEntry] path keeps the menu alive to surface the failure (partial seam: drops
               // out without the KSA assemblies).
               InstallMenuFallback();
   ```

   Its compile-gated seam (partial methods dropped when `KSA.dll` is absent) is a build-time technique, see
   [Game-free half and compile-gated Game half](../../build/build-a-mod-without-game-references/build-a-mod-without-game-references-2.md).
7. **Set up logging** (game category, own file) here or at immediate load: [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md).
8. **Defer scene-dependent work** to the first frame hook: patch `Universe`/viewport code now, but resolve
   vehicles and celestials later.

## Complete example

| File | Role |
|---|---|
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs) | Version check, transpiler install with stock fallback |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) | Per-feature validation and guarded apply |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) | Ordered setup, pack registry freeze, hook installs |
| [src/KSArmory/Ksa/JitWarmup.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/JitWarmup.cs) | Background warm-up |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs) | Never-throws initialization with menu fallback |

## Variations and gotchas

- **This is not "game loaded".** Touching `Universe.CurrentSystem` or `Program.MainViewport` here fails; the
  asset set is loaded but `AttachGameData` has not merged part game data (KSArmory audits against
  `DeclaredParts` for that reason).
- **It fires once**; `LoadAll` has one call site.
- **Hooks are not wrapped.** An exception from your `OnFullyLoaded` propagates out of the postfix and ends
  startup; wrap the body or each installer ([Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)).
- **Order across mods is initialization order.** A mod that depends on yours sees your state; its
  `StarMapAllModsLoaded` runs after yours, and its entry-point code (including pack registration) already ran
  before your hook ([Required dependency with ImportedAssemblies](../../dependencies/declare-a-mod-dependency/declare-a-mod-dependency-1.md)).
- **Patches installed here miss anything that ran earlier.** If you must see `PrepareAll` or the first shader
  compiles, use [StarMapBeforeMain - patch code that runs during startup](./choose-a-starmap-startup-hook-1.md).
- **Frame hooks follow.** `StarMapBeforeGui`/`AfterGui` start only after the constructor finishes; they never run
  while the HUD is hidden.

## See also

- [StarMapBeforeMain - patch code that runs during startup](./choose-a-starmap-startup-hook-1.md), [StarMapImmediateLoad - register data before assets deserialize](./choose-a-starmap-startup-hook-2.md), [Signature checklist against StarMap's binding rules](./choose-a-starmap-startup-hook-4.md), [Choose and use the right StarMap lifecycle hook](./overview.md)
- [Startup lifecycle hooks (BeforeMain, ImmediateLoad, AllModsLoaded)](../../../../mods/starmap/features/startup-lifecycle-hooks.md), [StarMap entry class with per-hook fault containment](../../../../mods/ksarmory/features/starmap-entry-and-fault-containment.md),
  [Compile-gated game seam](../../../../mods/gatos/features/compile-gated-game-seam.md), [Game version check](../../../../mods/ksaero/features/game-version-check.md)
- [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md), [Warn when the running game build differs from the one a mod was tested on](../check-the-game-version/overview.md)
