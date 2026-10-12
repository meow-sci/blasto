---
type: Mod Feature
title: API surface record and assembly lock
description: Pinning the KSA build a mod compiles against with a hash lock, recording the exact game members the compiled DLL binds to from its metadata tables, and narrowing each KSA update to the changes that can affect the mod
mod: ksagolf
category: testing-and-tooling
techniques: [metadata-reader, decompiling, ci-checks]
hooks: []
lifecycle: []
game_types: [KSA.Program]
tags: [api-surface, metadata, assembly-lock, upgrade, decompile, ilspy, ci, version-watch]
---

KSA has no official modding API and its internals move between builds. KSAGolf answers "which
build do we target, and what breaks when it changes" with four artefacts: a lock file of SHA-256
digests for every referenced game assembly plus the build number; a generated list of every
external type and member the compiled DLL binds to, read from the DLL's own metadata; a private
mirror repository of the game assemblies and their ILSpy decompilation; and scripts that combine
them so an update produces a short checklist instead of a quarter-million-line diff.

Everything committed is names, signatures and hashes, never game code, so it is safe in a public
repository.[^golf-api-surface-sh]

## How it works

| Artefact or tool | What it does |
|---|---|
| `ksa-assemblies.lock` | `build 2026.9.22.5482` then `<sha256>  <dll>` for the 15 referenced DLLs[^golf-lock] |
| `tools/check-assemblies.sh` | Hashes the DLLs the csproj files actually reference (parsed from `$(KsaDllDir)...dll`) in the folder the build would resolve, and compares to the lock; `--game` checks the install (skipping `StarMap.API.dll`), `--update` rewrites the lock; `build.sh` runs `--game --quiet` on every build[^golf-check-assemblies] |
| `KsaBuild` stamp | The csproj copies the lock's `build` line into `AssemblyMetadata("KsaBuild")`, so the startup log prints built-for vs running build[^golf-csproj] |
| `tools/apisurface/Program.cs` | Opens the compiled `KSAGolf.dll` with `System.Reflection.Metadata`, walks `TypeReferences` and `MemberReferences`, resolves each to its owning assembly (chasing nested types), decodes signatures, filters to `KSA`, `Brutal`, `Bepu`, `StarMap` assemblies, and prints sorted markdown[^golf-apisurface] |
| `tools/api-surface.sh` | Builds the mod first (never trusts a stale DLL), regenerates `docs/KSA-API-SURFACE.md` or, with `--check`, fails CI when it differs[^golf-api-surface-sh][^golf-ci] |
| `docs/KSA-API-SURFACE.md` | Generated: 157 types and 393 members across 8 assemblies at this ref[^golf-api-surface-md] |
| `tools/decompile-assemblies.sh` | Runs a pinned `ilspycmd` over the mirror so two builds diff cleanly; warns that upgrading ILSpy or changing the assembly set rewrites the whole corpus[^golf-decompile] |
| `tools/ksa-api-diff.sh` | Two reports against the surface: members that vanished from the new corpus, and decompiled files of used types that changed (the half the compiler cannot see: same signature, new meaning)[^golf-api-diff] |
| `tools/check-ksa-version.sh` + `ksa-version.yml` | Daily scheduled job asks RocketWerkz's master server for the published build and opens an issue when it moves past the lock; deliberately not part of `build.sh`[^golf-version-check][^golf-version-workflow] |
| `tools/check-docs.sh` | Fails when any prose file names a KSA build other than the lock's[^golf-check-docs] |
| `tools/apidump/` | `MetadataLoadContext` reflection dumper (`types`, `grep`, `members`) for exploring the game API without running it[^golf-apidump] |
| `upgrade-ksa` skill | The written procedure: confirm the update, refresh `Import/`, sync and decompile the mirror, read the diff, fix, `--update` the lock, regenerate the surface, recheck `docs/BLOCKED-ON-KSA.md`[^golf-upgrade-skill] |

**Pinning a patched method into the surface.** A Harmony target found by name is invisible to the
metadata walk. Each patch class adds a never-called method that really calls its target, so the
compiler emits a `MemberRef`, the member appears in the surface, and a signature change in KSA
becomes a build error: `WorldReloadHook.PinTheSignature() => Program.OnGameLoaded();`,
`PadSurface.PinTheStep` (`Universe.ExecuteNextVehicleSolvers`), `CourseCollider.PinThePatches`
(`ConstraintSim` methods) and `FirstPersonHook.PinTheWalk` / `PinTheRender`
(`KittenLocomotion.StepGrounded` and the render calls).[^golf-worldreloadhook] `ViewDrawHook` has
no such method (it names its target with `nameof`, which emits no member reference), which is why
`ClearBuckets` is the one public patch target missing from the record; the private
`Program.OnFrameCelestials` cannot be pinned at all, and neither can the reflected
`<FixedController>k__BackingField`. See [Degrading Harmony patches](degrading-harmony-patches.md).

## Lifecycle and timing

Development and CI time only. The tools run before a release (`api-surface.sh --check`,
`check-assemblies.sh` in the CI `build` job) and on a schedule (`ksa-version.yml`, daily at 07:17
UTC). The only runtime trace is the startup log line comparing `KsaBuild` with the running KSA
assembly version.

## Key code

| Path | Role |
|---|---|
| [ksa-assemblies.lock](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/ksa-assemblies.lock) | Build number and digests |
| [tools/apisurface/Program.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/apisurface/Program.cs) | Metadata-table extractor |
| [tools/api-surface.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/api-surface.sh) | Regenerate or check the surface |
| [docs/KSA-API-SURFACE.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-API-SURFACE.md) | The generated record |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/check-assemblies.sh) | Lock verification and update |
| [tools/ksa-api-diff.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-api-diff.sh) | Update impact report |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.claude/skills/upgrade-ksa/SKILL.md) | Upgrade procedure |

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
// src/KSAGolf/Ksa/WorldReloadHook.cs L121-L124
    // Never called. It exists so the compiler emits a reference to the patched method, which is
    // what puts Program.OnGameLoaded in docs/KSA-API-SURFACE.md and turns a signature change in KSA
    // into a build error here rather than a reload that silently stops being noticed.
    private static void PinTheSignature() => Program.OnGameLoaded();
```

```bash
# tools/api-surface.sh L46-L61
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

`Program.OnGameLoaded` is the one game member deliberately referenced only to pin it into the
surface ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs));
it is still public static in build 2026.10.10.5554.

## Pitfalls and version notes

- The metadata walk sees only what the compiler bound. Members reached by reflection
  (`Program.OnFrameCelestials`, `Program._screenshotCapture`, the clutter `_statics` dictionary) and
  members named with `nameof(...)` are not in the surface. `ViewDrawHook` finds its target with
  `nameof(SuperMeshRenderSystem.ClearBuckets)`: a rename is still a compile error because `nameof`
  must resolve, but it compiles to a string, so `ClearBuckets` is absent from
  `docs/KSA-API-SURFACE.md` even though CLAUDE.md describes it as pinned, and `ksa-api-diff.sh` will
  not list its file. A real call in a never-run method (the `PinTheSignature` pattern) is what puts a
  member in the surface.[^golf-viewdrawhook][^golf-api-surface-md]
- The lock catches a changed DLL, not a changed meaning; `ksa-api-diff.sh` narrows the reading to
  the decompiled files of types the mod uses.[^golf-api-diff]
- Decompiler version and the set of assemblies in the mirror both rewrite the whole corpus; change
  either in a commit of its own.[^golf-decompile]
- The mirror the mod's CI uses (`LaurensDeV/ksa-game-assemblies`, private, deploy key) is a
  different repository from the one this knowledge base checks claims against; the lock pins
  2026.9.22.5482, this page was verified against 2026.10.10.5554.[^golf-lock]

## See also

- [Content-plus-code mod layout](content-and-code-mod-layout.md)
- [Packaging, deploy and release pipeline](packaging-and-release.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Save-load detection](save-load-detection.md)
- [Decompile every build into a private git repo with one ilspycmd version kept across updates and diff only what your mod touches](../../../howto/testing-and-tooling/game-updates/decompile-the-game-for-reference/decompile-the-game-for-reference-2.md) - how-to article demonstrating this feature
- [Pin the KSA build with a hashed assemblies lock, stamp it into the mod and watch for new builds](../../../howto/testing-and-tooling/game-updates/pin-the-game-build-with-a-hashed-lock/pin-the-game-build-with-a-hashed-lock.md) - how-to article demonstrating this feature
- [Generate an API surface document from the built DLL and pin reflected Harmony targets](../../../howto/testing-and-tooling/game-updates/track-the-game-api-your-mod-uses/track-the-game-api-your-mod-uses.md) - how-to article demonstrating this feature
- [Upgrade by locking, decompiling both builds, diffing the API surface and updating the lock last](../../../howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-1.md) - how-to article demonstrating this feature

[^golf-lock]: ksa-assemblies.lock
[^golf-check-assemblies]: tools/check-assemblies.sh
[^golf-api-surface-sh]: tools/api-surface.sh
[^golf-apisurface]: tools/apisurface/Program.cs
[^golf-api-surface-md]: docs/KSA-API-SURFACE.md
[^golf-api-diff]: tools/ksa-api-diff.sh
[^golf-decompile]: tools/decompile-assemblies.sh
[^golf-version-check]: tools/check-ksa-version.sh
[^golf-version-workflow]: .github/workflows/ksa-version.yml
[^golf-check-docs]: tools/check-docs.sh
[^golf-upgrade-skill]: .claude/skills/upgrade-ksa/SKILL.md
[^golf-apidump]: tools/apidump/Program.cs
[^golf-worldreloadhook]: src/KSAGolf/Ksa/WorldReloadHook.cs
[^golf-viewdrawhook]: src/KSAGolf/Ksa/ViewDrawHook.cs
[^golf-ci]: .github/workflows/ci.yml
[^golf-csproj]: src/KSAGolf/KSAGolf.csproj
