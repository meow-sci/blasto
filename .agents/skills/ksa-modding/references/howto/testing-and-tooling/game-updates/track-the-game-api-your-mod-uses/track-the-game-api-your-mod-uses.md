---
type: How-To
title: Generate an API surface document from the built DLL and pin reflected Harmony targets
description: Read the TypeRef and MemberRef tables of your compiled mod into a committed document that CI checks, and turn reflected Harmony targets into compile-time dependencies with never-called pin methods
category: testing-and-tooling
group: game-updates
topic: track-the-game-api-your-mod-uses
difficulty: advanced
prerequisites: [dotnet-sdk, harmony]
demonstrated_by:
  - mods/ksarmory/features/api-surface-tracking
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksagolf/features/api-surface-and-assembly-lock
techniques: [metadata-analysis, harmony-prefix, ci-pipeline]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, harmony-postfix KSA.Program.OnGameLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Vehicle, KSA.Program, KSA.SunbloomRenderer, KSA.PartModelRenderer]
tags: [game-update, api-surface, metadata, harmony, pin, ci, drift]
---

Outcome: a committed `docs/KSA-API-SURFACE.md` that lists every external KSA, Brutal, Bepu and StarMap type and member the mod binds to (at the pinned ref, KSArmory's lists 297 types and 801 members across 11 assemblies), a CI step that fails when it is stale, and Harmony targets that are part of that list so a signature change becomes a compile error.
Demonstrated by KSArmory ([tools/apisurface/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/apisurface/Program.cs#L40-L114)), with the same tooling in KSACars and KSAGolf. Prerequisites: the .NET SDK and a mod that patches or calls game members.

## Background: how the game does it

A KSA update changes thousands of lines and nearly none of it matters to one mod; the list of what matters is exactly what the compiler bound. Two kinds of dependency need different handling. Direct calls leave a MemberRef in the assembly's metadata, so they are tracked for free. A Harmony target found by name (`AccessTools.Method(typeof(Program), "OnFrameCelestials", ...)`) leaves none, so a rename silently stops the patch from applying.
The patched members in this mod are `Vehicle.PrepareWorker(SimStep)` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)), `Program.OnGameLoaded` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599)), `SunbloomRenderer.Render` ([current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326)) and `PartModelRenderer.UpdateRenderData` ([current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L807)); all are public, so they can be pinned. `Program.OnFrameCelestials` is private ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701)) and cannot.

## Lifecycle and timing

Nothing here runs inside the game. The extractor reads the built assembly after `dotnet build`; `--check` runs in CI and in the pre-push script; the pin methods are compiled into the mod but never executed, so they cost nothing at run time and have no frame position. The patches they pin install at mod load (`startup:all-mods-loaded`); see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for where each patched method runs.

## Steps

1. **Write an extractor that reads metadata, not source.** Open the DLL with `PEReader` and `MetadataReader`, map each `AssemblyReference` to its name, and walk every `TypeReference` out to its owning assembly (a nested type's scope is its declaring `TypeRef`, so chase the chain):

   ```csharp
   // tools/apisurface/Program.cs L40-L77
   using var stream = File.OpenRead(path);
   using var pe = new PEReader(stream);
   var md = pe.GetMetadataReader();
   var provider = new Signatures();

   // assembly ref handle -> simple name
   var assemblyOf = new Dictionary<AssemblyReferenceHandle, string>();
   foreach (var handle in md.AssemblyReferences)
       assemblyOf[handle] = md.GetString(md.GetAssemblyReference(handle).Name);

   bool Wanted(string assembly) => prefixes.Any(p => assembly.StartsWith(p, StringComparison.Ordinal));

   // Walk out to the assembly that owns a type reference. A nested type's scope is its declaring
   // TypeRef rather than an AssemblyRef, so this has to chase the chain.
   (string Assembly, string Name)? ResolveType(TypeReferenceHandle handle)
   {
       var parts = new List<string>();
       while (true)
       {
           var tr = md.GetTypeReference(handle);
           var ns = md.GetString(tr.Namespace);
           var name = md.GetString(tr.Name);
           parts.Insert(0, string.IsNullOrEmpty(ns) ? name : ns + "." + name);

           var scope = tr.ResolutionScope;
           if (scope.Kind == HandleKind.AssemblyReference)
           {
               var asm = assemblyOf[(AssemblyReferenceHandle)scope];
               return (asm, string.Join("+", parts));
           }
           if (scope.Kind == HandleKind.TypeReference) { handle = (TypeReferenceHandle)scope; continue; }
           return null;    // ModuleRef or the current module: not an external dependency
       }
   }

   // assembly -> type -> set of members
   var surface = new SortedDictionary<string, SortedDictionary<string, SortedSet<string>>>(StringComparer.Ordinal);
   ```

2. **Record every type named at all, and every member actually bound.** Types appear even when only used as a parameter or a cast; members come from `MemberReference`s whose parent is a type reference, formatted through a small `ISignatureTypeProvider`. Keep only assemblies whose names start with `KSA`, `Brutal`, `Bepu` or `StarMap`. This captures exactly which overload bound:

   ```csharp
   // tools/apisurface/Program.cs L88-L108
   // Every type we name at all, even if we never call a member on it (a parameter type, a cast).
   foreach (var handle in md.TypeReferences)
       if (ResolveType(handle) is { } t)
           Touch(t.Assembly, t.Name);

   // Every member we actually bind to.
   foreach (var handle in md.MemberReferences)
   {
       var mr = md.GetMemberReference(handle);
       if (mr.Parent.Kind != HandleKind.TypeReference) continue;   // generic instantiations: below
       if (ResolveType((TypeReferenceHandle)mr.Parent) is not { } t) continue;

       var name = md.GetString(mr.Name);
       var text = mr.GetKind() switch
       {
           MemberReferenceKind.Method => FormatMethod(name, mr.DecodeMethodSignature(provider, null)),
           MemberReferenceKind.Field => $"{mr.DecodeFieldSignature(provider, null)} {name}",
           _ => name,
       };
       Touch(t.Assembly, t.Name, text);
   }
   ```

3. **Always rebuild before extracting, and support a check mode.** A stale DLL would report a false match. With `--check` the script diffs the generated text against the committed file and fails with a pointer to regenerate; CI and the pre-push script run the check form:

   ```bash
   # tools/api-surface.sh L29-L61
   MOD_DLL="$REPO_ROOT/src/KSArmory/bin/Release/net10.0/KSArmory.dll"

   # Always, not only when the assembly is missing. The surface is read from the compiled DLL, so
   # checking against a stale one reports a match that says nothing about the code as it stands --
   # and an incremental build costs about as long as the check itself.
   echo "building the mod first (the surface is read from the compiled assembly)..."
   "$REPO_ROOT/tools/build.sh" >/dev/null
   [[ -f "$MOD_DLL" ]] || { echo "error: $MOD_DLL not found; run ./tools/build.sh" >&2; exit 1; }

   # Build the extractor rather than `dotnet run` it: run rebuilds on every invocation, and its
   # argument forwarding can swallow an argument.
   dotnet build -c Release -v quiet --nologo "$REPO_ROOT/tools/apisurface/ApiSurface.csproj" >/dev/null
   EXTRACTOR="$REPO_ROOT/tools/apisurface/bin/Release/net10.0/apisurface"
   [[ -x "$EXTRACTOR" ]] || { echo "error: apisurface did not build" >&2; exit 1; }

   generated="$("$EXTRACTOR" "$MOD_DLL")"

   if (( CHECK )); then
       if [[ ! -f "$OUT" ]]; then
           echo "error: $(basename "$OUT") does not exist; run ./tools/api-surface.sh" >&2
           exit 1
       fi
       if ! diff -q <(printf '%s\n' "$generated") "$OUT" >/dev/null; then
           echo "error: docs/KSA-API-SURFACE.md is stale — the mod's KSA dependencies changed." >&2
           echo >&2
           diff <(printf '%s\n' "$generated") "$OUT" | head -40 >&2
           echo >&2
           echo "rerun ./tools/api-surface.sh and commit the result" >&2
           exit 1
       fi
       echo "API surface matches the code ($(grep -c '^- `' "$OUT") members)"
       exit 0
   fi
   ```

4. **Pin every Harmony target you can with a never-called method.** A private static method whose body calls the patched member makes the compiler emit the reference: the member appears in the surface, and a signature change is a build error. The body is never executed, so use it only for the compiler:

   ```csharp
   // src/KSArmory/Ksa/AttitudeHook.cs L433-L437
       // Never called. It exists so the compiler emits a reference to the patched method, which is
       // what puts Vehicle.PrepareWorker in docs/KSA-API-SURFACE.md and turns a signature change in
       // KSA into a build error here rather than a rocket that quietly stops steering.
       private static void PinTheSignature(Vehicle vehicle, SimStep step) => vehicle.PrepareWorker(step);
   }
   ```

   For static targets, the call needs no instance; for instance targets whose receiver you do not have, call through a null variable:

   ```csharp
   // src/KSArmory/Ksa/WorldReloadHook.cs L124-L128

       // Never called. It exists so the compiler emits a reference to the patched method, which is
       // what puts Program.OnGameLoaded in docs/KSA-API-SURFACE.md and turns a signature change in KSA
       // into a build error here rather than a reload that silently stops being noticed.
       private static void PinTheSignature() => Program.OnGameLoaded();
   ```

   ```csharp
   // src/KSArmory/Ksa/CloudPassHook.cs L41-L50

       /// <summary>
       /// Puts <c>SunbloomRenderer.Render</c> in this assembly's metadata. Never called.
       /// </summary>
       public static void PinTheSignature()
       {
           SunbloomRenderer? never = null;
           never?.Render(default, null!, 0);
       }
   ```

5. **Degrade where you cannot pin.** A patch on a private method cannot be pinned (no accessible member to call), so resolve it by name, log once and run without it if it is gone. The cost of that choice is that nothing checks it at compile time, which is why the failure has to degrade rather than break:

   ```csharp
   // src/KSArmory/Ksa/PreRenderHook.cs L62-L75
           try
           {
               // Private, so by name: there is no way to write this that a compiler would check, which
               // is exactly why the failure has to degrade rather than break.
               MethodInfo? target = AccessTools.Method(typeof(Program), "OnFrameCelestials",
                                                       [typeof(double)]);

               if (target is null)
               {
                   Trouble = "KSA has no Program.OnFrameCelestials(double) to hook";
                   Log.Warn($"stepping after the render on frames with no UI: {Trouble}");
                   return;
               }
   ```

6. **Know what the surface cannot see.** `nameof` and reflection by string leave no reference; they are invisible to the extractor, and so are behaviour changes behind an unchanged signature. Use the diff of decompiled sources for the latter (see [Keep a searchable decompiled copy of the game](../decompile-the-game-for-reference/overview.md)).

7. **Dump ad hoc.** A small `apidump` tool that loads the game DLLs into a `MetadataLoadContext` and lists types or dumps members by substring answers "what does `KSA.Vehicle` expose" without the game running ([tools/apidump/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/apidump/Program.cs)).

## Complete example

| File | Role |
|---|---|
| [tools/apisurface/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/apisurface/Program.cs) | MetadataReader walk of TypeRefs and MemberRefs |
| [tools/api-surface.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/api-surface.sh) | Build, extract, write or `--check` |
| [docs/KSA-API-SURFACE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-API-SURFACE.md#L1-L12) | The generated record |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L433-L437) | Example pin method |
| [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs) | The unpinnable, degrading case |
| [tools/ksa-api-diff.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-api-diff.sh) | Intersects the surface with a decompiled-corpus diff |

## Variations and gotchas

- **Only accessible members can be pinned.** A `public` method, virtual or static, can be called from your assembly; a `private` one cannot.
- **Generated files are safe to commit.** The surface carries names and signatures of your own dependencies and says nothing about how KSA implements them, the same property as the hashes in the assemblies lock.
- **Pins are not execution.** Never call a pin method; some bodies would throw (a null receiver).
- **Generic instantiations** need separate handling in the extractor ("generic instantiations: below" in the source).
- **Game updates.** After an update, regenerate: a diff of this file against the old one is itself the list of API movement that your own code caused; the diff of the game against it is the list of movement you must read (see [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md)).

## See also

- [Overview](overview.md)
- [Keep a searchable decompiled copy of the game next to your mod](../decompile-the-game-for-reference/overview.md), [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md), [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md)
- [Test Harmony patches without running the game](../../offline-tests/test-harmony-patches-without-the-game/overview.md), [Isolate patch failures](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
- Mod features: [KSArmory API surface tracking](../../../../mods/ksarmory/features/api-surface-tracking.md), [KSACars API surface pinning and diff](../../../../mods/ksacars/features/api-surface-pinning-and-diff.md), [KSAGolf API surface and assembly lock](../../../../mods/ksagolf/features/api-surface-and-assembly-lock.md)
