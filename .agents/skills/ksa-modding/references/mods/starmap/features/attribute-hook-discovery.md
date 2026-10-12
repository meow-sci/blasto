---
type: Mod Feature
title: Discover lifecycle hooks by attribute type name
description: StarMap finds a mod's [StarMapMod] class and its hook methods by comparing attribute type names rather than Type identity, validates each signature with StarMap's own attribute instance, and invokes hooks by reflection on one shared instance
mod: starmap
category: getting-started
techniques: [starmap-lifecycle, reflection]
hooks:
  - reflection StarMapModAttribute / StarMapMethodAttribute subclasses (by type name)
  - reflection MethodInfo.Invoke on the [StarMapMod] instance
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Mod]
tags: [starmap, attributes, reflection, lifecycle, api, starmap-api]
---

A StarMap mod never implements an interface or derives from a base class. It marks one class with
`[StarMapMod]` and marks public methods on it with hook attributes from the `StarMap.API` package.
StarMap discovers both by reflection, and it compares attribute **type names**, not `Type`
objects, so the match does not depend on which copy or build of `StarMap.API` the mod compiled
against as long as the class names are the same. The C# surface of `StarMap.API` at the pinned
commit is unchanged since release 0.3.6 apart from one doc comment, which is why mods in this
corpus still reference the 0.3.6 package (for example KSAero).[^ksaero-csproj]

## How it works

1. **Known hooks.** The `ModLoader` constructor scans StarMap's own `StarMap.API` assembly for
   concrete subclasses of `StarMapMethodAttribute` that are valid on methods, creates one instance
   of each, and stores them in `RegisteredMethodAttributes` keyed by type name (for example
   `"StarMapAfterGuiAttribute"`).[^sm-modloader]
2. **Mod class.** `RuntimeMod.InitializeMod` takes the first type in the entry assembly that has
   any attribute whose type name equals `"StarMapModAttribute"`, and creates it with
   `Activator.CreateInstance` (public parameterless constructor). That single instance receives
   every hook call for the rest of the session.[^sm-runtimemod]
3. **Hook methods.** For each method from `ModType.GetMethods()` (public instance and static
   methods, inherited ones included), every attribute whose type name is a registered key is looked
   up; StarMap's own instance of that attribute runs `IsValidSignature(method)`, and on success
   `ModRegistry.AddModMethod` appends `(attribute, instance, method)` to the list for that attribute
   type.[^sm-modregistry]
4. **Special cases.** `StarMapBeforeMain` and `StarMapImmediateLoad` methods are also stored on the
   `RuntimeMod` (`BeforeMainAction`, `PrepareSystemsAction`) because they are called per mod rather
   than broadcast. Only one of each is kept per mod (the last one registered).
5. **Dispatch.** Each StarMap patch fetches the list for its attribute type with
   `ModRegistry.Get<TAttribute>()` and calls `method.Invoke(instance, args)` for each entry, in
   registration order.

### Hook signatures

All hooks return `void`. The API documentation says "instance method"; `IsValidSignature` checks
only the return type and parameters.[^sm-baseattributes][^sm-ongui][^sm-onframe]

| Attribute | Required parameters | Fires at |
|---|---|---|
| `[StarMapMod]` | (class) | Marks the mod class |
| `[StarMapBeforeMain]` | none | Right after this mod's hooks are bound, before KSA's `Main` |
| `[StarMapImmediateLoad]` | `KSA.Mod definingMod` | Harmony prefix on `KSA.Mod.PrepareSystems` for this mod |
| `[StarMapAllModsLoaded]` | none | Harmony postfix on `KSA.ModLibrary.LoadAll` |
| `[StarMapBeforeGui]` | `double dt` | Harmony prefix on `KSA.Program.OnDrawUiFrame` |
| `[StarMapAfterGui]` | `double dt` | Harmony postfix on `KSA.Program.OnDrawUiViewports` |
| `[StarMapAfterOnFrame]` | `double currentPlayerTime, double dtPlayer` | Harmony postfix on `KSA.Program.OnFrame` |
| `[StarMapUnload]` | none | After KSA's `Main` returns |

Exact timing and game state for each are in [startup hooks](startup-lifecycle-hooks.md),
[frame hooks](frame-lifecycle-hooks.md) and [unload](unload-and-shutdown.md).

## Lifecycle and timing

Discovery runs once per mod in `InitializeMod`, during `StarMapCore.Init`
(`startup:starmap-before-main`). The registry is read on every dispatch, on the main thread. Its
lists are never modified after startup, so a mod cannot add or remove hooks at run time; mods
that need conditional behaviour check a flag inside the hook.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L15-L44) | Builds `RegisteredMethodAttributes` from StarMap.API |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162) | Finds the mod class, instantiates it, binds methods, calls `BeforeMain` |
| [StarMap.Core/ModRepository/ModRegistry.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModRegistry.cs#L34-L71) | Per-attribute method lists and lookup |
| [StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L10-L132) | `StarMapMod`, `StarMapMethodAttribute`, startup and unload attributes |
| [StarMap.API/OnGuiAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnGuiAttributes.cs#L30-L75) | `StarMapBeforeGui`, `StarMapAfterGui` |
| [StarMap.API/OnFrameAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/OnFrameAttributes.cs#L35-L45) | `StarMapAfterOnFrame` |

## Snippets

```csharp
// StarMap.Core/ModRepository/ModLoader.cs L21-L44
private (string attributeName, StarMapMethodAttribute attribute)? ConvertAttributeType(Type attrType)
{
    if ((Activator.CreateInstance(attrType) as StarMapMethodAttribute) is not StarMapMethodAttribute attrObject) return null;
    return (attrType.Name, attrObject);
}

public ModLoader(AssemblyLoadContext coreAssemblyLoadContext)
{
    _coreAssemblyLoadContext = coreAssemblyLoadContext;

    Assembly coreAssembly = typeof(StarMapModAttribute).Assembly;

    RegisteredMethodAttributes = coreAssembly
        .GetTypes()
        .Where(t =>
            typeof(StarMapMethodAttribute).IsAssignableFrom(t) &&
            t.IsClass &&
            !t.IsAbstract &&
            t.GetCustomAttribute<AttributeUsageAttribute>()?.ValidOn.HasFlag(AttributeTargets.Method) == true
        )
        .Select(ConvertAttributeType)
        .OfType<(string attributeName, StarMapMethodAttribute attribute)>()
        .ToDictionary();
}
```

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

```csharp
// StarMap.API/BaseAttributes.cs L72-L80
public class StarMapImmediateLoadAttribute : StarMapMethodAttribute
{
    public override bool IsValidSignature(MethodInfo method)
    {
        return method.ReturnType == typeof(void) &&
               method.GetParameters().Length == 1 &&
               method.GetParameters()[0].ParameterType == typeof(Mod);
    }
}
```

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `KSA.Mod` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L14) | The only game type in the API surface: parameter type required by `StarMapImmediateLoad`; `StarMap.API` compiles against the KSA reference package for it[^ksa-mod][^sm-api-csproj] |

## Pitfalls and version notes

- **A wrong signature is silently ignored.** No log line is written. purrTTY declares
  `[StarMapImmediateLoad] public void OnImmediateLoad()` with no `KSA.Mod` parameter, so StarMap
  never calls it (harmless there, since the body only logs).[^purrtty-terminalmod]
- **Only public methods count.** `GetMethods()` without binding flags skips private, protected and
  internal methods, even if they carry a hook attribute.
- **Only the first `[StarMapMod]` class** in the entry assembly is used; hook attributes on any other
  class are ignored. Public hook methods inherited from a base class are found.
- **Static methods pass the check.** `IsValidSignature` does not test `IsStatic`, and
  `MethodInfo.Invoke` ignores the instance for static methods, so a public static hook is called
  too (inferred from the code; the documentation asks for instance methods).
- **Exceptions are not caught.** `GetTypes()`, the constructor, `BeforeMain` and every dispatched
  hook run without a `try`. A type in the entry assembly that cannot load (for example one deriving
  from a type in an absent optional dependency) throws `ReflectionTypeLoadException` out of
  StarMap's startup; an exception in a frame hook ends the game. Wrap risky work yourself, as KSAero
  does around its patching.
- **Matching by simple name only.** Namespace and assembly are not compared, so any attribute class
  named `StarMapAfterGuiAttribute` would match.
- **README typo.** The `StarMapImmediateLoad` example in `StarMap.API/README.md` shows
  `[StarMapBeforeMain]` on a method taking `KSA.Mod`; use `[StarMapImmediateLoad]`.[^sm-api-readme]

## See also

- [Startup lifecycle hooks](startup-lifecycle-hooks.md)
- [Frame lifecycle hooks](frame-lifecycle-hooks.md)
- [Unload hook and shutdown order](unload-and-shutdown.md)
- [Order mod loading with a ModDependencies graph](mod-dependency-graph.md)
- [Bind plugins by attribute type name the way StarMap does](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-4.md) - how-to article demonstrating this feature
- [Diagnose a StarMap hook that never fires with a signature checklist](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-4.md) - how-to article demonstrating this feature
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature
- [Build a minimal StarMap mod by hand from the discovery rules](../../../howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-1.md) - how-to article demonstrating this feature
- [Follow the boot sequence: host process, load contexts and hook binding](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md) - how-to article demonstrating this feature

[^sm-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-modregistry]: StarMap.Core/ModRepository/ModRegistry.cs
[^sm-baseattributes]: StarMap.API/BaseAttributes.cs
[^sm-ongui]: StarMap.API/OnGuiAttributes.cs
[^sm-onframe]: StarMap.API/OnFrameAttributes.cs
[^sm-api-readme]: StarMap.API README
[^sm-api-csproj]: StarMap.API/StarMap.API.csproj
[^purrtty-terminalmod]: purrtty purrTTY.GameMod/TerminalMod.cs
[^ksaero-csproj]: KSAero/KSAero.csproj
[^ksa-mod]: KSA.Mod (decompiled)
