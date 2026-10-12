---
type: Mod Feature
title: API surface pinning and KSA upgrade diffing
description: Record every KSA type and member the compiled mod binds to from its own metadata tables, pin Harmony targets into that record with never-called PinTheSignature methods, lock the game assemblies by SHA-256, and narrow a KSA update's decompiled diff to what the mod actually touches
mod: ksacars
category: testing-and-tooling
techniques: [api-surface-diff, decompile, hash-lock]
hooks: []
lifecycle: []
game_types: []
tags: [tooling, api-surface, metadata, harmony, upgrade, decompile, ilspy, ci, version-check]
---

KSA has no stable modding API, and the parts KSACars binds to move between builds. The repository
answers that with a chain of tools: the exact set of KSA members the compiled DLL uses is written to
`docs/KSA-API-SURFACE.md` (163 types and 370 members at this ref), every Harmony target is forced
into that set, the game assemblies the mod builds against are locked by hash, a daily job notices a
new KSA build before it is installed, and an upgrade diff is narrowed to the decompiled files that
define the types on the list.[^ksacars-surface-doc][^ksacars-claude]

## How it works

**Read the surface from metadata, not source.** `tools/apisurface` opens the compiled
`KSACars.dll` with `System.Reflection.Metadata`, walks every `TypeReference` (chasing nested types
to their `AssemblyReference`) and every `MemberReference`, decodes method and field signatures,
keeps only assemblies whose names start with `KSA`, `Brutal`, `Bepu` or `StarMap`, and prints a
sorted markdown list. "The compiler already resolved all of that"; reading what it bound cannot
drift from the code.[^ksacars-apisurface] `tools/api-surface.sh` rebuilds the mod first, runs the
extractor, and either rewrites the file or, with `--check`, fails CI when the committed list is
stale.[^ksacars-apisurface-sh]

**Pin Harmony targets.** A Harmony patch found by reflection leaves no metadata reference, so a
renamed target would silently stop patching. Each hook on a public method therefore has a
`PinTheSignature` method that is never called and simply invokes the target, which makes the
compiler emit a `MemberReference`: the target appears in the surface, and a signature change in KSA
becomes a build error.[^ksacars-physicshook] Two patches target private methods
(`LensColourHook`, `RailsHook`); those cannot be pinned, so each checks what it found at install
and switches itself off with a warning.[^ksacars-lenscolourhook]

**Lock the assemblies.** `ksa-assemblies.lock` holds the KSA build number and the SHA-256 of each
referenced DLL (KSA.dll, Brutal.*, BepuUtilities, Planet.Render.Core, StarMap.API, 0Harmony).
`tools/check-assemblies.sh` compares the resolved folder, the installed game (`--game`) or any
folder against it and `--update` records new digests; `tools/sync-import.sh` copies the game's
DLLs into `Import/` and reports drift. The csproj also stamps the lock's build into the assembly
for the [log](./mod-log-file.md).[^ksacars-lock][^ksacars-checkassemblies][^ksacars-syncimport]

**Notice updates early.** `tools/check-ksa-version.sh` asks RocketWerkz's master server
(`http://ksa-master1.rocketwerkz.com:8082/version`, overridable with `KSA_VERSION_URL`) for the
published build and compares it with the lock; an unreachable server is "not a reason to fail".
`.github/workflows/ksa-version.yml` runs it daily and opens an issue instead of failing.[^ksacars-checkversion][^ksacars-version-workflow]

**Upgrade.** `tools/sync-assemblies.sh` and `tools/decompile-assemblies.sh` refresh a private
`ksa-game-assemblies` checkout's DLLs and ilspycmd output (pinned ilspycmd version; deterministic
output so a git diff is real change).[^ksacars-syncassemblies][^ksacars-decompile]
`tools/ksa-api-diff.sh <checkout> [ref]` parses the surface file and reports (1) surface members
that no longer appear in their assembly's decompiled sources and (2) decompiled files defining
surface types that changed since the ref, because "a member that kept its name and signature and
changed its behaviour compiles clean and is wrong at runtime".[^ksacars-apidiff] The `upgrade-ksa`
Claude skill walks through the whole procedure, including the seven places the build number is
written.[^ksacars-upgrade-skill] `tools/apidump` is a reflection dumper (`types`, `grep`, `members`)
over the game DLLs via `MetadataLoadContext` for exploring the API.[^ksacars-apidump]

## Lifecycle and timing

Build and CI time only; nothing runs in game. `PinTheSignature` methods are compiled but never
executed. The hooks they pin are installed at `[StarMapAllModsLoaded]` (see the individual
features).

## Key code

| Path | Role |
|---|---|
| [tools/apisurface/Program.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/apisurface/Program.cs) | Metadata-table extractor |
| [tools/api-surface.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/api-surface.sh) | Regenerate or `--check` the surface |
| [docs/KSA-API-SURFACE.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/docs/KSA-API-SURFACE.md) | The generated list |
| [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs#L91-L94) | Example `PinTheSignature` |
| [ksa-assemblies.lock](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/ksa-assemblies.lock) | Build number and DLL hashes |
| [tools/check-assemblies.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/check-assemblies.sh), [tools/sync-import.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/sync-import.sh), [tools/sync-assemblies.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/sync-assemblies.sh) | Lock checks and refresh |
| [tools/decompile-assemblies.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/decompile-assemblies.sh), [tools/ksa-api-diff.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/ksa-api-diff.sh) | Decompile and narrow the diff |
| [tools/check-ksa-version.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/check-ksa-version.sh), [.github/workflows/ksa-version.yml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.github/workflows/ksa-version.yml) | Published-build watch |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.claude/skills/upgrade-ksa/SKILL.md) | The procedure, as an agent skill |
| [tools/apidump/Program.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/apidump/Program.cs) | Reflection dumper |

## Snippets

```csharp
// src/KSACars/Ksa/PhysicsHook.cs L91-L94
    // Never called. It exists so the compiler emits a reference to the patched method, which is what
    // puts Vehicle.PrepareWorker in docs/KSA-API-SURFACE.md and turns a signature change in KSA into a
    // build error here.
    private static void PinTheSignature(Vehicle vehicle, SimStep step) => vehicle.PrepareWorker(step);
```

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

None at runtime. The surface lists what the mod touches; the tools read KSA's DLLs and decompiled
sources offline.

## Pitfalls and version notes

- **Reflection and Harmony targets are invisible to the compiler.** Without a pin, a renamed method
  only shows up as a feature that silently stopped working. Pin every public target; for private
  ones, check the method was found and log when it was not.
- **Members accessed by reflection** (`_manualControlInputs`, `_engineFlags`,
  `KittenRenderable._characterAvatar`, `Program._screenshotCapture`) are not in the surface either;
  each is type-checked at runtime and degrades with a warning.
- **The surface catches shape changes, not meaning changes.** Read the changed decompiled files
  `ksa-api-diff.sh` lists; that is the half the compiler cannot find.
- **Do not upgrade ilspycmd during a KSA upgrade**, and keep the set of decompiled assemblies fixed,
  or the corpus diff becomes unreadable churn.[^ksacars-decompile]
- The scripts expect the private mirror's sources under `current/src`; the corpus used to verify this
  page (meow-sci `ksa-game-assemblies`) keeps them under `current/decomp`.
- Game DLLs are RocketWerkz's copyrighted files: the lock and the surface hold only hashes and
  names, and are safe to publish; the DLLs are not.

## See also

- [Headless simulation tests](./headless-sim-tests.md)
- [Package, deploy and release](./package-deploy-release.md)
- [Mod log file](./mod-log-file.md)
- [Inject wheel forces from a Vehicle.PrepareWorker prefix](./prepareworker-impulse-injection.md)
- [Decompile every build into a private git repo with one ilspycmd version kept across updates and diff only what your mod touches](../../../howto/testing-and-tooling/game-updates/decompile-the-game-for-reference/decompile-the-game-for-reference-2.md) - how-to article demonstrating this feature
- [Pin the KSA build with a hashed assemblies lock, stamp it into the mod and watch for new builds](../../../howto/testing-and-tooling/game-updates/pin-the-game-build-with-a-hashed-lock/pin-the-game-build-with-a-hashed-lock.md) - how-to article demonstrating this feature
- [Generate an API surface document from the built DLL and pin reflected Harmony targets](../../../howto/testing-and-tooling/game-updates/track-the-game-api-your-mod-uses/track-the-game-api-your-mod-uses.md) - how-to article demonstrating this feature
- [Upgrade by locking, decompiling both builds, diffing the API surface and updating the lock last](../../../howto/testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/upgrade-a-mod-to-a-new-ksa-build-1.md) - how-to article demonstrating this feature

[^ksacars-physicshook]: src/KSACars/Ksa/PhysicsHook.cs
[^ksacars-apisurface]: tools/apisurface/Program.cs
[^ksacars-apisurface-sh]: tools/api-surface.sh
[^ksacars-surface-doc]: docs/KSA-API-SURFACE.md
[^ksacars-apidump]: tools/apidump/Program.cs
[^ksacars-apidiff]: tools/ksa-api-diff.sh
[^ksacars-decompile]: tools/decompile-assemblies.sh
[^ksacars-syncassemblies]: tools/sync-assemblies.sh
[^ksacars-syncimport]: tools/sync-import.sh
[^ksacars-checkassemblies]: tools/check-assemblies.sh
[^ksacars-checkversion]: tools/check-ksa-version.sh
[^ksacars-lock]: ksa-assemblies.lock
[^ksacars-version-workflow]: .github/workflows/ksa-version.yml
[^ksacars-upgrade-skill]: .claude/skills/upgrade-ksa/SKILL.md
[^ksacars-lenscolourhook]: src/KSACars/Ksa/LensColourHook.cs
[^ksacars-claude]: CLAUDE.md
