---
type: Mod Feature
title: Record the exact KSA API a mod binds to from its compiled metadata
description: A small System.Reflection.Metadata tool lists every KSA type and member the built mod references, committed as a generated document, checked in CI and used to narrow a KSA update's decompiled diff to what can actually break
mod: ksarmory
category: testing-and-tooling
techniques: [metadata-reader, generated-docs, decompiled-diff, signature-pinning]
hooks: []
lifecycle: []
game_types: [KSA.Vehicle, KSA.Program, KSA.SunbloomRenderer, KSA.PartModelRenderer]
tags: [api-surface, metadata, ksa-update, decompile, harmony, ci]
---

When KSA updates, the decompiled game changes by thousands of lines and nearly none of it matters
to any one mod. KSArmory turns that into a short checklist: a 200-line tool reads the compiled
`KSArmory.dll`'s TypeRef and MemberRef metadata tables and writes every external KSA, Brutal, Bepu
and StarMap type and member it binds to into `docs/KSA-API-SURFACE.md` (297 types and 801 members
across 11 assemblies at the pinned ref).[^surface-doc] CI fails when that file is stale, and a
companion script intersects the list with a diff of the decompiled corpus. Harmony targets found by
reflection, which the compiler never sees, are forced into the record with never-called "pin"
methods.

## How it works

1. **Extract from metadata, not source.** [tools/apisurface/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/apisurface/Program.cs)
   opens the DLL with `PEReader`/`MetadataReader`, maps each `AssemblyReference` to its name, walks
   every `TypeReference` out to its owning assembly (chasing nested-type scopes), and records every
   `MemberReference` whose parent is a type reference, formatting method and field signatures with
   a small `ISignatureTypeProvider<string, object?>`. Only assemblies whose names start with `KSA`,
   `Brutal`, `Bepu` or `StarMap` are kept (overridable with `--assemblies`).[^apisurface] This
   captures exactly which overload bound and every type named only as a parameter or cast.
2. **Generate and check.** [tools/api-surface.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/api-surface.sh)
   always rebuilds the mod first (a stale DLL would report a false match), builds the extractor,
   then either writes the document or, with `--check`, diffs and fails.[^api-surface-sh] CI and
   `tools/check-all.sh` run the `--check` form.[^ci]
3. **Pin reflected Harmony targets.** A patch target found with `GetMethod`/`AccessTools` by name
   leaves no MemberRef. Each hook class carries a private method that is never called but calls the
   patched method directly, so the compiler emits the reference: `AttitudeHook` pins
   `Vehicle.PrepareWorker(SimStep)`, `CloudPassHook` pins `SunbloomRenderer.Render`,
   `LooseBodyDrawHook` pins `PartModelRenderer.UpdateRenderData`, `RoundBodyDrawHook` pins
   `Vehicle.UpdateRenderData`, and `WorldReloadHook` pins `Program.OnGameLoaded`.[^attitude-hook][^cloud-pass-hook][^loose-body-hook][^world-reload-hook]
   A signature change in KSA then becomes a compile error instead of a patch that silently stops
   applying. This only works for accessible members: `PreRenderHook` patches the private
   `Program.OnFrameCelestials`, which cannot be pinned and is resolved by name with a logged
   fallback instead.[^pre-render-hook]
4. **Diff the game against the list.** [tools/decompile-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/decompile-assemblies.sh)
   decompiles the referenced assemblies with a pinned `ilspycmd` into the private assemblies
   repository so consecutive builds produce a readable `git diff`.[^decompile]
   [tools/ksa-api-diff.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-api-diff.sh)
   then answers two questions: which surface members no longer appear in their assembly, and which
   decompiled files defining surface types this update touched (the ones whose meaning may have
   changed under an unchanged signature).[^api-diff]
5. **Ad-hoc inspection.** [tools/apidump/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/apidump/Program.cs)
   loads the game DLLs into a `MetadataLoadContext` and lists types or dumps members by substring,
   for answering "what does `KSA.Vehicle` expose" without the game running.[^apidump]

The `upgrade-ksa` skill drives the whole sequence after an update.[^upgrade-skill]

## Lifecycle and timing

Nothing here runs inside the game. The extractor reads the built assembly after `dotnet build`;
`--check` runs in CI and in the pre-push `check-all.sh`; the diff tools run by hand during an
upgrade. The pin methods are compiled into the mod but never executed, so they cost nothing at
runtime and have no frame position.

## Key code

| Path | Role |
|---|---|
| [tools/apisurface/Program.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/apisurface/Program.cs#L40-L114) | MetadataReader walk of TypeRefs and MemberRefs |
| [tools/api-surface.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/api-surface.sh#L29-L66) | Build, extract, write or `--check` |
| [docs/KSA-API-SURFACE.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-API-SURFACE.md) | The generated record |
| [tools/ksa-api-diff.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-api-diff.sh) | Missing members and touched files for an update |
| [tools/decompile-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/decompile-assemblies.sh) | Deterministic decompile into the private corpus |
| [src/KSArmory/Ksa/AttitudeHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/AttitudeHook.cs#L432-L436) | Example pin method |

## Snippets

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

```csharp
// src/KSArmory/Ksa/AttitudeHook.cs L433-L437
    // Never called. It exists so the compiler emits a reference to the patched method, which is
    // what puts Vehicle.PrepareWorker in docs/KSA-API-SURFACE.md and turns a signature change in
    // KSA into a build error here rather than a rocket that quietly stops steering.
    private static void PinTheSignature(Vehicle vehicle, SimStep step) => vehicle.PrepareWorker(step);
}
```

```bash
# tools/api-surface.sh L44-L61
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

## Game APIs touched

The pinned patch targets, all confirmed accessible in build 2026.10.10.5554:

| Member | Declaration |
|---|---|
| `Vehicle.PrepareWorker(SimStep)` | `public virtual` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)) |
| `Vehicle.UpdateRenderData(IViewport, int)` | `public virtual` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713))[^ksa-vehicle] |
| `Program.OnGameLoaded()` | `public static` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599))[^ksa-program] |
| `Program.OnFrameCelestials(double)` | `private`, cannot be pinned (Program.cs L2701) |
| `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | `public` ([current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326))[^ksa-sunbloom] |
| `PartModelRenderer.UpdateRenderData(IViewport, int)` | `public static` ([current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L807))[^ksa-partmodelrenderer] |

## Pitfalls and version notes

- **Reflection is invisible.** Private fields and methods reached by name (`_screenshotCapture`,
  `_manualControlInputs`, `OnFrameCelestials` and others in `KsaWorld`) are not in the record, so a
  rename there compiles clean; those sites need their own runtime fallbacks and a read of the
  decompiled diff.
- **Members on generic instantiations are skipped.** The walk ignores MemberRefs whose parent is a
  TypeSpec (the comment says "below", but no later code handles them), so calls on constructed
  generic KSA types are recorded as type names only.[^apisurface]
- **Meaning changes are not signature changes.** The record makes renames mechanical; the
  "changed files" half of `ksa-api-diff.sh` exists because a member can keep its signature and
  change units or frame.[^api-diff]
- **Decompiler churn.** Upgrading `ilspycmd` or changing the set of reference assemblies rewrites
  the corpus wholesale; the script asks for either to be done in a commit of its own.[^decompile]
- XML the mod loads (part definitions, Core asset schemas) is not covered; the upgrade skill diffs
  Core's XML separately.[^upgrade-skill]

## See also

- [Pin the KSA build with a hashed assemblies lock file](./ksa-assemblies-lock.md)
- [Repository static checks](./repo-static-checks.md)
- [PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)
- [Armoury pack registration API](./armoury-pack-registration-api.md)
- [Decompile every build into a private git repo with one ilspycmd version kept across updates and diff only what your mod touches](../../../howto/testing-and-tooling/game-updates/decompile-the-game-for-reference/decompile-the-game-for-reference-2.md) - how-to article demonstrating this feature
- [Generate an API surface document from the built DLL and pin reflected Harmony targets](../../../howto/testing-and-tooling/game-updates/track-the-game-api-your-mod-uses/track-the-game-api-your-mod-uses.md) - how-to article demonstrating this feature
- [Upgrade by locking, decompiling both builds, diffing the API surface and updating the lock last](../../../howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-1.md) - how-to article demonstrating this feature

[^apisurface]: tools/apisurface/Program.cs
[^api-surface-sh]: tools/api-surface.sh
[^surface-doc]: docs/KSA-API-SURFACE.md
[^api-diff]: tools/ksa-api-diff.sh
[^decompile]: tools/decompile-assemblies.sh
[^apidump]: tools/apidump/Program.cs
[^attitude-hook]: src/KSArmory/Ksa/AttitudeHook.cs
[^cloud-pass-hook]: src/KSArmory/Ksa/CloudPassHook.cs
[^loose-body-hook]: src/KSArmory/Ksa/LooseBodyDrawHook.cs
[^world-reload-hook]: src/KSArmory/Ksa/WorldReloadHook.cs
[^pre-render-hook]: src/KSArmory/Ksa/PreRenderHook.cs
[^upgrade-skill]: .claude/skills/upgrade-ksa/SKILL.md
[^ci]: .github/workflows/ci.yml
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-sunbloom]: KSA.SunbloomRenderer (decompiled, build 2026.10.10.5554)
[^ksa-partmodelrenderer]: KSA.PartModelRenderer (decompiled, build 2026.10.10.5554)
