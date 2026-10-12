---
type: How-To
title: Diagnose a StarMap hook that never fires with a signature checklist
description: Check a hook against StarMap's name-based binding rules, exact parameter lists and firing-point table, and prove it ran with a one-line probe per hook
category: getting-started
group: lifecycle
topic: choose-a-starmap-startup-hook
approach: 4
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/attribute-hook-discovery
  - mods/ksarmory/features/starmap-entry-and-fault-containment
techniques: [starmap-lifecycle, reflection]
hooks: [starmap StarMapMod, starmap StarMapBeforeGui, starmap StarMapAfterOnFrame]
lifecycle:
  - startup:starmap-before-main
  - startup:immediate-load
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:after-on-frame
  - event:shutdown
game_types: [KSA.Program, KSA.ModLibrary, KSA.Mod]
tags: [starmap, diagnostics, hooks, signature, attributes, troubleshooting]
---

Your hook is attributed and compiles, yet nothing logs. StarMap gives no diagnostics: a method that does not meet
its binding rules is simply never registered. This article is the checklist of those rules, the table of where
each hook is actually dispatched (so you know what "fires" means for it), and a probe that proves which ones ran.
It complements the three "which hook" articles ([StarMapBeforeMain - patch code that runs during startup](./choose-a-starmap-startup-hook-1.md),
[StarMapImmediateLoad - register data before assets deserialize](./choose-a-starmap-startup-hook-2.md), [StarMapAllModsLoaded - work that needs the full mod set](./choose-a-starmap-startup-hook-3.md)) and the model in
[Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md).

## Background: how the game does it

StarMap matches **by attribute type name**, not by `Type`. `RuntimeMod.InitializeMod` takes the first type in the
entry assembly with any attribute whose type name equals `"StarMapModAttribute"`, instantiates it with
`Activator.CreateInstance`, then for every method from `ModType.GetMethods()` looks at the attributes whose type
names are registered hooks and keeps the method only if the attribute's own `IsValidSignature` passes:[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L129-L162
public bool InitializeMod(ModRegistry modRegistry)
{
    var modClass = ModAssembly.GetTypes().FirstOrDefault(type => type.GetCustomAttributes().Any(attr => attr.GetType().Name == typeof(StarMapModAttribute).Name));
    if (modClass is null) return false;

    ModType = modClass;

    var modObject = Activator.CreateInstance(ModType);
    if (modObject is null) return false;
    ModInstance = modObject;
    Initialized = true;

    var classMethods = ModType.GetMethods();

    foreach (var classMethod in classMethods)
    {
        var stringAttrs = classMethod.GetCustomAttributes().Select((attr) => attr.GetType().Name).Where(ModLoader.RegisteredMethodAttributes.Keys.Contains);
        foreach (var stringAttr in stringAttrs)
        {
            var attr = ModLoader.RegisteredMethodAttributes[stringAttr];

            if (!attr.IsValidSignature(classMethod)) continue;

            modRegistry.AddModMethod(ModId, attr, modObject, classMethod);
        }
    }

    if (modRegistry.TryGetMod(ModId, out var modInfo) && modInfo.BeforeMainAction is MethodInfo action)
    {
        action.Invoke(modInfo.ModInstance, []);
    }

    return true;
}
```

`GetMethods()` without binding flags returns **public** instance and static methods (including inherited ones);
non-public methods are never seen. `StarMapBeforeMain` and `StarMapImmediateLoad` are also stored on the `RuntimeMod`
(only the last registered one of each per mod is kept) because they are called per mod, not broadcast.[StarMap.Core/ModRepository/ModRegistry.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModRegistry.cs#L34-L71)
Dispatch is `method.Invoke(instance, args)` per registered method, in registration order, with no
`try`/`catch`.[StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L12-L48)

## Lifecycle and timing

Where each hook is dispatched (checked against StarMap 4dc60f8 and KSA build 2026.10.10.5554, with the
`Program.cs` line numbers):

| Hook | Dispatch point | State when it fires |
|---|---|---|
| construction of your `[StarMapMod]` class | `InitializeMod`, from `ModLoader.Init` before `Program.Main` | No game: field initializers run here, so build heavy objects in a hook instead |
| `StarMapBeforeMain` | direct call at the end of `InitializeMod` | no `Program` yet |
| `StarMapImmediateLoad` | prefix on `Mod.PrepareSystems`, from `ModLibrary.PrepareAll` (L899) | manifest read, asset bundles not yet loaded (`PreloadAssetBundles` is L900) |
| `StarMapAllModsLoaded` | postfix on `ModLibrary.LoadAll` (L942) | every mod's XML loaded; `AssignDefaults` (L943), `LoadDefaultSystem` (L1069), `IsLoaded = true` (L1317) still ahead |
| `StarMapBeforeGui` | prefix on `Program.OnDrawUiFrame` (L2267), inside `if (DrawUI)` | `PrepareFrame` done, ImGui frame open |
| `StarMapAfterGui` | postfix on `Program.OnDrawUiViewports` (L2268), inside `if (DrawUI)` | as above |
| `StarMapAfterOnFrame` | postfix on `Program.OnFrame` (L2238), every return path | after `PostRender` on a normal frame; also after the early returns (exit requested, or `IsLoaded` still false) |
| `StarMapUnload` | `ModLoader.Dispose`, from `StarMapCore.DeInit`, after `App.Run` returned | `Program` disposed; all Harmony patches already removed |

The two GUI hooks do not run on frames with the HUD hidden (`DrawUI` false); `StarMapAfterOnFrame` is the only
per-frame hook guaranteed every frame.[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L268-L305)[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2268)

## Steps

Walk this checklist top to bottom:

1. **Class.** `[StarMapMod]` is on a class with a public parameterless constructor (`Activator.CreateInstance(Type)`
   needs that; the class's own visibility is not checked, though every mod in the corpus makes it `public`), in the **entry
   assembly** named by `EntryAssembly` (other assemblies are not scanned). Only the *first* matching type is used;
   hook attributes on any other class are ignored.
2. **Visibility.** Hook methods are `public`. Private, internal and protected methods are not returned by
   `GetMethods()`.
3. **Exact signature.** All return `void`. Anything else is skipped without a message:

   | Attribute | Required parameters |
   |---|---|
   | `[StarMapBeforeMain]` | none |
   | `[StarMapImmediateLoad]` | exactly one, of type `KSA.Mod` |
   | `[StarMapAllModsLoaded]` | none |
   | `[StarMapBeforeGui]` | `double` |
   | `[StarMapAfterGui]` | `double` |
   | `[StarMapAfterOnFrame]` | `double`, `double` |
   | `[StarMapUnload]` | none |

   These come from the attributes' `IsValidSignature`.[StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L10-L132)[StarMap.API/OnGuiAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnGuiAttributes.cs#L30-L75)[StarMap.API/OnFrameAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnFrameAttributes.cs#L35-L45) The classic trap is a
   parameterless `[StarMapImmediateLoad]` (purrTTY, gatOS and the unscience template have one that is never called).
   The API documentation also says "instance method"; the checks do not test `IsStatic`, and `GetMethods()`
   returns public static methods too, so static hooks bind: OhMyStars' entry class declares all its hooks
   `public static`.[src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L9-L48)
4. **Attribute name.** Matching is by simple type name (`StarMapAfterGuiAttribute`); a look-alike class with the
   same name would match. The `StarMap.API` package you compile against does not need to be the same build as
   StarMap's.
5. **Entry assembly loaded?** The DLL must be named `<EntryAssembly>.dll` in the mod folder and the mod must be
   enabled (see [Folder and manifest rules as StarMap consumes them](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)); otherwise StarMap skips the mod silently.
6. **One per attribute for startup hooks.** If two methods carry `[StarMapBeforeMain]` or `[StarMapImmediateLoad]`
   on one class, only the last registered is called.
7. **Throwing.** `MethodInfo.Invoke` wraps exceptions in `TargetInvocationException`; nothing catches them, so an
   exception in a hook ends startup or the frame. If a hook seems to stop mid-way, look for an exception
   ([Keep mod exceptions out of KSA's frame loop](../contain-exceptions-in-starmap-hooks/overview.md)).
8. **Prove it with a probe.** Console output works in every hook, including `StarMapBeforeMain`:

   ```csharp
   // adapted from StarMap.API/BaseAttributes.cs signatures; Console.WriteLine is safe before KSA logging exists
   using StarMap.API;

   namespace MyMod;

   [StarMapMod]
   public sealed class Probe
   {
       private static int _frame;
       private static void Mark(string s) => Console.WriteLine($"[probe] {DateTime.Now:HH:mm:ss.fff} {s}");

       [StarMapBeforeMain]      public void A() => Mark("BeforeMain");
       [StarMapImmediateLoad]   public void B(KSA.Mod mod) => Mark($"ImmediateLoad {mod.Id}");
       [StarMapAllModsLoaded]   public void C() => Mark("AllModsLoaded");
       [StarMapBeforeGui]       public void D(double dt) { if (_frame == 0) Mark("BeforeGui (first)"); }
       [StarMapAfterGui]        public void E(double dt) { if (_frame == 0) Mark("AfterGui (first)"); }
       [StarMapAfterOnFrame]    public void F(double t, double dt) { if (_frame++ == 0) Mark("AfterOnFrame (first)"); }
       [StarMapUnload]          public void G() => Mark("Unload");
   }
   ```

   Expected order in the StarMap console: BeforeMain, ImmediateLoad, AllModsLoaded, then the three frame lines
   on the first frame (BeforeGui and AfterGui only if the HUD is visible), and Unload at exit. A missing line tells
   you which signature or visibility rule you broke.

## Complete example

[src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs) is the fullest real set of six attributed methods; its page lists the fault handling around each
([Per-hook try/catch feeding a shared fault counter, unpatch at ten](../contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-1.md)). The binding code is [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs) and the dispatchers
[StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs).

## Variations and gotchas

- **README typo.** The `StarMap.API` README shows `[StarMapBeforeMain]` on the `StarMapImmediateLoad` example;
  use `[StarMapImmediateLoad]`.
- **Hooks cannot be added later.** The registry is fixed at startup; use a flag inside the hook for conditional
  behaviour.
- **No mod-ready event.** Nothing marks "the scene is ready"; latch on the first frame hook.
- **`Unload` runs after Harmony was unpatched**; the probe still prints.

## See also

- [StarMapBeforeMain - patch code that runs during startup](./choose-a-starmap-startup-hook-1.md), [StarMapImmediateLoad - register data before assets deserialize](./choose-a-starmap-startup-hook-2.md), [StarMapAllModsLoaded - work that needs the full mod set](./choose-a-starmap-startup-hook-3.md), [Choose and use the right StarMap lifecycle hook](./overview.md)
- [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md), [StarMap entry class with per-hook fault containment](../../../../mods/ksarmory/features/starmap-entry-and-fault-containment.md)
- [Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md), [Clean up on game exit and relaunch the game from a mod](../clean-up-on-game-exit/overview.md)
