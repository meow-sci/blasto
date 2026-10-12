---
type: Mod Feature
title: Attribute duck typing as an inter-mod API
description: Consumer mods copy look-alike attribute classes instead of referencing ShaderExtensions; a Program.Main prefix scans every AssemblyLoadContext, matches attributes by full name, closes generic registration methods over the consumer's struct and fills its static delegate fields
mod: shaderextensions
category: external-io
techniques: [inter-mod-api, reflection, assembly-load-context, harmony-prefix]
hooks: [harmony-prefix KSA.Program.Main]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Program, KSA.KeyHash]
tags: [inter-mod, reflection, attributes, assembly-load-context, api-design, kittenextensions]
---

ShaderExtensions publishes its C# API without a shared contract assembly. A consumer copies four
tiny attribute declarations (and optionally delegate types) into its own code, in the
`ShaderExtensions` namespace, and decorates its structs. ShaderExtensions finds them by name at
startup and wires them up with reflection. The consumer needs no compile-time reference to
`ShaderExtensions.dll`, and nothing in its code fails to load without it; its lookup delegates
simply stay `null`.[^sx-readme] Whether to also declare a StarMap dependency is the consumer's
choice: MPFX compiles without the DLL but lists ShaderExtensions as a required mod
([MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L64-L80)).[^mpfx-csproj]
ShaderExtensions itself consumes KittenExtensions the same way: it declares
`KittenExtensions.KxAssetAttribute` internally rather than using the one in `KittenExtensions.dll`
([ShaderExtensions/Attributes.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Attributes.cs#L8-L15),
[KittenExtensions/Attributes.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Attributes.cs#L6-L10)).[^sx-attributes] [^kx-attributes]

## How it works

1. **Find candidate assemblies.** `AssetEx.Init` walks `AssemblyLoadContext.All` and every assembly in
   each context, and treats an assembly as a consumer if
   `asm.GetType("ShaderExtensions.SxUniformBufferAttribute")` is non-null. StarMap gives every mod its
   own load context, so the scan has to cross contexts
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L24-L27)).[^sx-assetex]
2. **Read attributes without instantiating them.** `RegisterAll` enumerates types (falling back to
   `ReflectionTypeLoadException.Types` when some fail to load) and reads
   `GetCustomAttributesData()`. Matching compares `attr.AttributeType.FullName` with the full name of
   ShaderExtensions' own internal attribute, so the two attribute types never need to be the same
   `Type`; the constructor argument (the XML element name) is validated as a non-empty string
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L58-L126)).
3. **Close a generic method over the consumer's struct.** `UniformBufferEx.AddUniformBuffer` gets the
   private `AddUniformBufferGeneric<T>`, calls `MakeGenericMethod(type)` and invokes it through
   `CreateDelegate<Action<string>>()`; push constants do the same through `PushConstantEx`
   ([ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L12-L20),
   [ShaderExtensions/PushConstantEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PushConstantEx.cs#L9-L17)).[^sx-uniformbufferex] [^sx-pushconstantex]
4. **Fill static delegate fields by signature.** Inside the generic method, static fields of `T` whose
   attribute full name is the lookup attribute get a delegate created from a ShaderExtensions method
   chosen by the delegate's `Invoke` return type. Because `MethodInfo.CreateDelegate(fieldType)` only
   needs a compatible signature, the consumer can declare its own delegate types (MPFX names them
   `MPFXBufferLookup`, `MPFXSpanLookup<T>` and so on)
   ([ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L42-L73),
   [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs#L7-L25)).[^mpfx-buffers]
5. **Call through the delegate.** At runtime the consumer calls
   `MyStruct.LookupSpan(KeyHash.Make("Id"))`, a direct delegate call into ShaderExtensions' generic
   static method with no reflection on the hot path.

The same idea appears in KittenExtensions, whose asset scan gates on its own attribute names and
registers asset classes found in any context
([KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L18-L23),
[KittenExtensions/AssetEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/AssetEx.cs#L124-L138)).[^kx-assetex]

## Lifecycle and timing

- **Hook: Harmony prefix on `Program.Main` (closest vocabulary value `startup:starmap-before-main`).** StarMap first loads every
  mod and runs their `[StarMapBeforeMain]` methods inside `core.Init()`, then invokes the game's entry
  point ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L56)). ShaderExtensions applies its
  patches from `[StarMapBeforeMain]`, so its `Program.Main` prefix runs after every mod assembly is in
  a load context and before the `Program` constructor parses any XML
  ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L20-L25),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703)).[^sm-gamesurveyer] [^sx-patches] [^ksa-program]
- **Why not `StarMapAllModsLoaded`.** That attribute fires from a postfix on `ModLibrary.LoadAll`,
  after the XML has been deserialized, which is too late to add element types.

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L27) | Cross-context scan |
| [ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L78-L126) | Attribute match by full name, argument validation |
| [ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L12-L74) | Generic closure and delegate binding |
| [ShaderExtensions/Attributes.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Attributes.cs#L8-L30) | The attribute classes consumers copy (and the KittenExtensions ones ShaderExtensions copies) |
| [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs#L7-L25) | A consumer's copy of the contract (MPFX) |

## Snippets

```csharp
// ShaderExtensions/AssetEx.cs L78-L105
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

                if (!ValidateArg(attr, 0, out string xmlElement, out var err))
                    return fail(err);

                UniformBufferEx.AddUniformBuffer(type, xmlElement);
            }
            else if (attr.AttributeType.FullName == typeof(SxPushConstantAttribute).FullName)
            {
                if (attr.ConstructorArguments.Count < 1)
                    return fail("not enough arguments");

                if (!ValidateArg(attr, 0, out string xmlElement, out var err))
                    return fail(err);

                PushConstantEx.AddPushConstant(type, xmlElement);
            }
```

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

```csharp
// MPFX/Buffers.cs L7-L25 (consumer copy of the contract)
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.Main(string[])` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1693-L1703) | Prefix target: the last point before the `Program` constructor |
| `KeyHash.Make` | [current/decomp/KSA/KeyHash.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyHash.cs#L15) | Key type in every lookup delegate signature |

## Pitfalls and version notes

- **The gate only checks one name.** An assembly is scanned only if it defines
  `ShaderExtensions.SxUniformBufferAttribute`. A consumer that only needs push constants must still
  declare that class or its `[SxPushConstant]` structs are ignored.
- **`unmanaged` is not enforced at registration.** C#'s `unmanaged` constraint is a compiler check;
  the runtime only enforces its `struct` part, so `MakeGenericMethod` accepts a struct with
  reference-type fields and registration succeeds (checked with a .NET 10 test program). The failure
  comes later, when the data is reinterpreted as bytes: `MappedMemory.AsSpan<T>` (span lookups) and
  `PushConstantBindingReference<T>.GetData` (every push) use `MemoryMarshal` casts that throw
  `ArgumentException` for such types
  ([current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs#L101-L108),
  [ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L157-L158)).
  Keep the structs blittable.[^brutal-mappedmemory] [^sx-shaderbinding]
- **Assemblies loaded later are missed.** The scan runs once; an assembly loaded after `Program.Main`
  starts is never registered.
- **Element name clashes.** If two mods register the same element name, the second injection fails
  validation with a warning and is skipped.
- **Unset delegates.** Calling a lookup before ShaderExtensions has run, or without it installed,
  is a `NullReferenceException`; test the field for `null` first.
- **Copy the namespace exactly.** Matching is by `FullName`, so the attribute classes must be in the
  `ShaderExtensions` namespace with the exact class names; accessibility does not matter.

## See also

- [Mod-defined uniform buffer bindings](custom-uniform-buffers.md)
- [Mod-defined push constants](push-constants.md)
- [Custom XML asset types and injected child elements](xml-asset-types-and-element-injection.md)
- [Duck-type an API: copy look-alike attributes and bind by full name across load contexts](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-3.md) - how-to article demonstrating this feature
- [StarMapBeforeMain: install patches on code that runs during startup](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-1.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^mpfx-csproj]: MPFX/MPFX.csproj
[^sx-attributes]: ShaderExtensions/Attributes.cs
[^kx-attributes]: KittenExtensions/Attributes.cs
[^sx-assetex]: ShaderExtensions/AssetEx.cs
[^sx-uniformbufferex]: ShaderExtensions/UniformBufferEx.cs
[^sx-pushconstantex]: ShaderExtensions/PushConstantEx.cs
[^mpfx-buffers]: MPFX/Buffers.cs
[^kx-assetex]: KittenExtensions/AssetEx.cs
[^sm-gamesurveyer]: StarMap/GameSurveyer.cs
[^sx-patches]: ShaderExtensions/Patches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sx-shaderbinding]: ShaderExtensions/ShaderBinding.cs
[^brutal-mappedmemory]: Brutal.VulkanApi.Abstractions.MappedMemory (decompiled, build 2026.10.10.5554)
