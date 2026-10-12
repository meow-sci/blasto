---
type: How-To
title: Bind plugins by attribute type name the way StarMap does
description: Copy StarMap's host-side pattern - register your own attribute instances by type name, match plugin attributes by name, then validate signatures with the host's instance - so plugins need not share your assembly
category: getting-started
group: dependencies
topic: share-types-between-mods
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/starmap/features/attribute-hook-discovery]
techniques: [reflection, starmap-lifecycle]
hooks: [reflection System.Reflection.MethodInfo.Invoke]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Mod]
tags: [attributes, reflection, name-based-binding, plugins, starmap, host-side]
---

StarMap itself is the cleanest example of discovering plugins without sharing types. A StarMap mod never implements an
interface; it marks a class with `[StarMapMod]` and methods with hook attributes from the `StarMap.API` package. StarMap
compares attribute **type names**, not `Type` objects, so the match does not depend on which copy or build of
`StarMap.API` the mod compiled against, as long as the class names are the same. This article shows the host side of
that contract so you can build the same for your own mod. The related client-side form is
[Duck-typed attributes copied into the consumer (plus: Scan every AssemblyLoadContext with GetCustomAttributesData)](./share-types-between-mods-3.md); the in-practice guide for StarMap's own hooks is
[Signature checklist against StarMap's binding rules](../../lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-4.md).

## Background: how the game does it

The host scans its **own** `StarMap.API` assembly for the known hook attributes. At 4dc60f8 a mod's `StarMap.API`
reference happens to resolve to that same copy (the mod context asks the game context, where `StarMap.Core` already
loaded it, before the mod folder), so a `Type` comparison would also work today; comparing names makes the binding
independent of that resolution order and of the API build the mod compiled against, which is what a plugin host whose
clients may carry their own copy of the attribute classes needs. The host keeps its own instance of each attribute class and asks that
instance to validate the plugin's method signature, which avoids any dependence on the plugin's attribute instance.[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L15-L44)

## Lifecycle and timing

Discovery runs once per mod in `InitializeMod` during `StarMapCore.Init` (`startup:starmap-before-main`). The registry
lists are never modified after startup, so a plugin cannot add or remove hooks at run time. Dispatch is
`method.Invoke(instance, args)` per registered method, on the main thread, in registration order.

## Steps

1. **Build the table of known attributes from your own assembly**, keyed by type name, one instance each:[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L21-L44)

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

2. **Let each attribute class own its signature rule:** an abstract base with `IsValidSignature(MethodInfo)`:[StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L72-L80)

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

3. **Pick the plugin class by name, instantiate it, bind its methods by name and validated signature:**[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)

   ```csharp
   // StarMap.Core/ModRepository/RuntimeMod.cs L129-L152
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
   ```

4. **Store bindings per attribute type** and dispatch with `MethodInfo.Invoke`. `ModRegistry.AddModMethod` appends
   `(attribute, instance, method)` to a list keyed by the attribute type; the dispatching patches fetch the list with
   `ModRegistry.Get<TAttribute>()`.[StarMap.Core/ModRepository/ModRegistry.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModRegistry.cs#L34-L71)
5. **Document the quirks of your binding** because plugin authors cannot see them: only public methods, only the first
   matching class, a wrong signature is skipped silently, simple-name matching ignores namespaces, and an exception in a
   plugin method is not caught.

## Complete example

[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs) (table of known attributes), [StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs) (the attribute classes and signature checks),
[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs) (class and method binding), [StarMap.Core/ModRepository/ModRegistry.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModRegistry.cs) (storage and lookup).

## Variations and gotchas

- **Simple-name matching only.** Namespace and assembly are not compared; any attribute class named
  `StarMapAfterGuiAttribute` would match.
- **Silent skips** for wrong signatures are a usability cost; consider logging a skipped candidate in your own host.
- **Only the first plugin class counts**, and `GetMethods()` without flags sees only public members.
- **Static methods pass**: the validators do not test `IsStatic`, and `GetMethods()` returns public static methods
  (OhMyStars' entry class declares all its hooks `public static`).
- **Contrast with approach 3**: ShaderExtensions scans *consumer* assemblies in other contexts with
  `GetCustomAttributesData`; StarMap instead already holds the plugin's assembly and uses live
  `GetCustomAttributes()` plus a type name test.

## See also

- [Duck-typed attributes copied into the consumer (plus: Scan every AssemblyLoadContext with GetCustomAttributesData)](./share-types-between-mods-3.md), [Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md), [Share types and design an inter-mod API between StarMap mods](./overview.md)
- [Discover lifecycle hooks by attribute type name](../../../../mods/starmap/features/attribute-hook-discovery.md)
- [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), [How StarMap launches KSA and loads your mod](../../loader/how-starmap-launches-ksa/overview.md)
