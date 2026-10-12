---
type: How-To
title: "Duck-type an API: copy look-alike attributes and bind by full name across load contexts"
description: Let consumer mods copy a few attribute classes into their own assembly while the host scans every AssemblyLoadContext with GetCustomAttributesData and wires delegates, so no assembly reference or mod dependency is needed
category: getting-started
group: dependencies
topic: share-types-between-mods
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/shaderextensions/features/duck-typed-attribute-api
  - mods/mpfx/features/duck-typed-buffer-structs
  - mods/ksa-aircrafthud/features/sx-push-constants-and-buffers
techniques: [reflection, inter-mod-api, harmony-prefix]
hooks:
  - harmony-prefix KSA.Program.Main
  - reflection System.Runtime.Loader.AssemblyLoadContext.All
  - reflection System.Reflection.CustomAttributeData
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Program]
tags: [duck-typing, attributes, assembly-load-context, reflection, shaderextensions, inter-mod-api]
---

ShaderExtensions publishes its C# API without a shared contract assembly. A consumer copies four tiny attribute
declarations into its own code, in the `ShaderExtensions` namespace, and decorates its structs. ShaderExtensions finds
them **by name** and wires them up with reflection. The consumer needs no compile-time reference to
`ShaderExtensions.dll` and nothing in its code fails to load without it; its lookup delegates simply stay `null`. This
is the opposite trade-off from [Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md): no assembly sharing and no type identity problem, at the
cost of coupling by name. MPFX and KSA_AircraftHUD are the consumers; MPFX also lists ShaderExtensions as a required
mod, AircraftHUD does not.

## Background: how the game does it

Every StarMap mod lives in its own `AssemblyLoadContext`, so a host cannot ask the runtime for "all types with
attribute X" through a single context. `AssemblyLoadContext.All` enumerates every context, and
`CustomAttributeData` describes an attribute without instantiating it, so the host can compare **names** and read the
constructor arguments of an attribute class it never loaded. The scan must run after every mod assembly is in a
context but before the game reads the asset XML that uses the new element names. The hook that gives exactly that
window is a Harmony prefix on `Program.Main`: StarMap loads every mod and runs their `StarMapBeforeMain` methods inside
`core.Init()`, and only then invokes the game's entry point.[StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L56)[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703)

## Lifecycle and timing

**Hook: Harmony prefix on `Program.Main` (closest vocabulary value `startup:starmap-before-main`).** ShaderExtensions
applies its patches from `[StarMapBeforeMain]`; the `Program.Main` prefix then runs after every mod assembly is loaded
and before the `Program` constructor parses any XML. Why not `StarMapAllModsLoaded`: that fires after `LoadAll` has
deserialized the XML, which is too late to add element types. The scan runs once, so an assembly loaded later is never
registered.

## Steps

1. **Copy the attribute shims into the consumer, in the `ShaderExtensions` namespace**, exactly as named (matching is
   by `FullName`; accessibility does not matter). Delegate types may be the consumer's own:[MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs#L7-L25)

   ```csharp
   // MPFX/Buffers.cs L7-L25
   namespace ShaderExtensions
   {
       [AttributeUsage(AttributeTargets.Struct)]
       internal class SxUniformBufferAttribute(string xmlElement) : Attribute;

       [AttributeUsage(AttributeTargets.Field)]
       internal class SxUniformBufferLookupAttribute() : Attribute;

       [AttributeUsage(AttributeTargets.Struct)]
       internal class SxPushConstantAttribute(string xmlElement) : Attribute;

       [AttributeUsage(AttributeTargets.Field)]
       internal class SxPushConstantLookupAttribute() : Attribute;

       public delegate BufferEx MPFXBufferLookup(KeyHash hash);
       public delegate MappedMemory MPFXMemoryLookup(KeyHash hash);
       public delegate Span<T> MPFXSpanLookup<T>(KeyHash hash) where T : unmanaged;
       public unsafe delegate T* MPFXPtrLookup<T>(KeyHash hash) where T : unmanaged;
   }
   ```

   AircraftHUD has the same block under its own delegate names (`AHBufferLookup`, ...).[ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L12-L33)
2. **Decorate a struct and declare the lookup field** on the same type:[MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs)

   ```csharp
   // MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs L7-L18
   namespace MPFX.Buffers
   {
       [StructLayout(LayoutKind.Sequential, Pack = 1)]
       [SxPushConstant("MPFXPushConstantsFloatBufferAsset")]
       public struct MPFXPushConstantsFloatBuffer
       {
           public float a;

           // lookup delegate fields must be static fields on the buffer element type
           [SxPushConstantLookup] public static MPFXSpanLookup<MPFXPushConstantsFloatBuffer> LookupSpan; // gives a Span<T> of length Size
       }
   }
   ```

3. **Host side: find consumers by probing for the gate type in every context**, then read attributes by name:[ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L24-L27)

   ```csharp
   // ShaderExtensions/AssetEx.cs L24-L27
               foreach (var alc in AssemblyLoadContext.All)
                   foreach (var asm in alc.Assemblies)
                       if (asm.GetType(typeof(SxUniformBufferAttribute).FullName) != null)
                           RegisterAll(asm);
   ```

   ```csharp
   // ShaderExtensions/AssetEx.cs L70-L90
           private static void RegisterAll(Assembly asm)
           {
               foreach (var type in GetTypes(asm))
                   foreach (var attr in type.GetCustomAttributesData())
                       RegisterAttr(type, attr);
           }

           private static bool RegisterAttr(Type type, CustomAttributeData attr)
           {
               bool fail(string message)
               {
                   DefaultCategory.Log.Warning($"invalid attribute {attr.AttributeType} on {type}: {message}");
                   return false;
               }

               if (attr.AttributeType.FullName == typeof(SxUniformBufferAttribute).FullName)
               {
                   if (attr.ConstructorArguments.Count < 1)
                       return fail("not enough arguments");
   ```

   `GetTypes` falls back to `ReflectionTypeLoadException.Types` so one unloadable type does not hide the rest.
4. **Close a generic method over the consumer's struct and invoke it through a delegate:**[ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L12-L20)

   ```csharp
   // ShaderExtensions/UniformBufferEx.cs L12-L20
           public static void AddUniformBuffer(Type type, string xmlElement)
           {
               var method = typeof(UniformBufferEx).GetMethod(
                 nameof(AddUniformBufferGeneric),
                 BindingFlags.Static | BindingFlags.NonPublic
               ).MakeGenericMethod(type);

               method.CreateDelegate<Action<string>>()(xmlElement);
           }
   ```

5. **Fill the static delegate fields by signature.** For each static field tagged with the lookup attribute, the host
   inspects the delegate's `Invoke` return type (`BufferEx`, `MappedMemory`, `Span<T>`, `T*`) and assigns a matching
   method with `CreateDelegate(fieldType)`. Because only the signature has to match, the consumer's delegate types can
   have any name.
6. **Call through the delegate at run time:** `MyStruct.LookupSpan(KeyHash.Make("Id"))` is a direct delegate call; no
   reflection on the hot path. Test the field for `null` first (the host may be absent or not yet run).
7. **Optionally declare the dependency too.** MPFX compiles without `ShaderExtensions.dll` but lists the mod as
   required so it loads after it ([Required dependency with ImportedAssemblies](../declare-a-mod-dependency/declare-a-mod-dependency-1.md)); AircraftHUD relies on the name matching alone.

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs) | `StarMapBeforeMain` applies the host patches |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs) | `Program.Main` prefix calls `AssetEx.Init` |
| [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs), [ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs) | Scan, validate, close generics, inject delegates |
| [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs), [MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXPushConstantsFloatBuffer.cs) | Consumer shims and a decorated struct |
| [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs) | A second consumer with different delegate names |

Cross-context scanning is also how KittenExtensions finds asset classes declared by other mods: its scan gates on its
own attribute names and registers asset classes found in any context. For a path that scans every context from a
`Program.Main` prefix, this approach *is* the answer to "discover plugins by attribute name across load contexts".

## Variations and gotchas

- **The gate checks one name.** An assembly is scanned only if it defines `ShaderExtensions.SxUniformBufferAttribute`;
  a consumer that only needs push constants must still declare that class.
- **`unmanaged` is not enforced at registration.** The compiler constraint is not checked through reflection;
  `MakeGenericMethod` accepts a struct with reference-type fields, and the failure comes later when data is
  reinterpreted as bytes (`MappedMemory.AsSpan<T>` throws). Keep structs blittable.
- **Assemblies loaded later are missed.** The scan runs once, in the `Program.Main` prefix.
- **Element name clashes.** If two mods register the same element name, the second injection fails validation with a
  warning and is skipped.
- **Unset delegates are `null`**, not an error at load; calling one before the host ran is a
  `NullReferenceException`.
- **By-name coupling.** A host that renames an attribute silently stops seeing consumers; document the names as ABI
  ([Text-only registration members with fault results, required dependency and a CI-checked API surface](./share-types-between-mods-2.md) shows how to guard a surface in CI).
- **Anchor misses abort the launch.** The host applies all its patches (including the `RenderGame` transpiler) inside
  `StarMapBeforeMain`, so a transpiler anchor miss throws out of `ModLoader.Init`; an exception in the scan itself
  (the `Program.Main` prefix body) propagates out of KSA's `Main`. Either way the game does not start ([StarMapBeforeMain - patch code that runs during startup](../../lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-1.md)).

## See also

- [Importer lists the contract assembly in ImportedAssemblies](./share-types-between-mods-1.md), [StarMap's name-based attribute binding](./share-types-between-mods-4.md), [Share types and design an inter-mod API between StarMap mods](./overview.md)
- [Attribute duck typing as an inter-mod API](../../../../mods/shaderextensions/features/duck-typed-attribute-api.md), [Uniform buffer and push constant structs bound by attribute name](../../../../mods/mpfx/features/duck-typed-buffer-structs.md),
  [ShaderExtensions push constants and uniform buffers from C#](../../../../mods/ksa-aircrafthud/features/sx-push-constants-and-buffers.md)
- feed-uniform-buffers-to-a-shaderextensions-shader (planned how-to), [Declare a dependency on another StarMap mod, required or optional](../declare-a-mod-dependency/overview.md)
