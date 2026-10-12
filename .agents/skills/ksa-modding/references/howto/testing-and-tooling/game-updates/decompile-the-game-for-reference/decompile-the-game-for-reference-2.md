---
type: How-To
title: Decompile every build into a private git repo with one ilspycmd version kept across updates and diff only what your mod touches
description: Decompile the game assemblies deterministically into a private repository so two builds produce a readable git diff, and narrow that diff to the files and members your mod's API surface names
category: testing-and-tooling
group: game-updates
topic: decompile-the-game-for-reference
approach: 2
difficulty: advanced
prerequisites: [dotnet-sdk, bash, git]
demonstrated_by:
  - mods/ksarmory/features/api-surface-tracking
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksagolf/features/api-surface-and-assembly-lock
techniques: [decompiling, git-diff, api-surface]
hooks: []
lifecycle: [event:game-update]
game_types: []
tags: [decompile, ilspycmd, diff, game-update, private-repo, api-surface]
---

Outcome: after a game update, one command decompiles the new assemblies into a private repository, `git diff` between two commits is the full record of what the game changed, and a second script reduces that diff to the members your mod binds to and the decompiled files that define the types it uses.
Demonstrated by KSArmory ([tools/decompile-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/decompile-assemblies.sh#L3-L32)) and, with the same scripts, KSACars and KSAGolf. Prerequisites: a private git repository holding the game assemblies (see [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md)) and an API surface list (see [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md)).

## Background: how the game does it

A KSA update changes thousands of lines across a quarter of a million, and almost none of it can affect one mod. The compiler already reports renamed members; what it cannot report is a member that kept its name and signature and changed meaning (units, a frame of reference, an enum's order, a method that started returning normalised values). Those are only visible in source, and only usefully visible as a diff. This corpus's own game checkout is the same idea: decompiled sources committed per build under `current/decomp` beside `current/dll` and `current/version.json` ([current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json#L1-L5), and for example [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396)).

## Lifecycle and timing

Development time, run by hand at upgrade time (`event:game-update`); nothing runs in the game. The decompile is committed together with the binaries so the pair stays consistent.

## Steps

1. **Decompile with one `ilspycmd` version, kept by convention.** The script's own comment says to pin one version across updates and prints the version it ran with, but nothing enforces it. ILSpy's output is deterministic for a given assembly and tool version, verified by decompiling twice and diffing, so a diff between two runs is real change. Two things make it churn anyway: upgrading `ilspycmd` (install one version and keep it across updates; the script prints `ilspycmd --version` so a change is visible, but it does not enforce a pin) and changing which assemblies are in `current/dll`, because the decompiler resolves cross-assembly types against `-r`. Going from 8 to 44 assemblies took `KSA.dll` from 250,187 to 224,074 lines with no game change; if you must do either, do it in its own commit:

   ```bash
   # tools/decompile-assemblies.sh L17-L29
   # ILSpy's output is deterministic for a given assembly and ilspycmd version, verified by
   # decompiling twice and diffing, so a diff between two runs is real change and not churn.
   #
   # Two things make it churn anyway, and both produce a diff far too large to read:
   #
   #   - Upgrading ilspycmd. Pin one version across updates.
   #   - Changing which assemblies are in current/dll. The decompiler resolves cross-assembly
   #     types against its -r path, so widening the set turns unresolved references into real
   #     names: going from 8 assemblies to 44 takes KSA.dll from 250,187 lines to 224,074 with no
   #     change in the game at all.
   #
   # If you have to do either, do it in its own commit with nothing else in it, so the next real
   # update still diffs cleanly against something.
   ```

2. **Refuse to write inside the public repository.** The output is the game developer's copyrighted code in source form; it goes in the private repository and nowhere else:

   ```bash
   # tools/decompile-assemblies.sh L47-L60
   TARGET="$(cd "$TARGET" && pwd)"
   # Decompiled game source must never land in the public repository, including via a symlink or a
   # checkout nested inside it.
   if [[ "$TARGET" == "$REPO_ROOT" || "$TARGET" == "$REPO_ROOT"/* ]]; then
       echo "error: $TARGET is inside this public repository." >&2
       echo "       Decompiled KSA source is RocketWerkz's copyrighted code and cannot go here." >&2
       exit 1
   fi

   command -v ilspycmd >/dev/null 2>&1 || {
       echo "error: ilspycmd not found" >&2
       echo "       dotnet tool install -g ilspycmd" >&2
       exit 1
   }
   ```

3. **Decompile whatever the mirror holds, minus libraries that publish their own source.** A corpus covering only your mod's assemblies is useless to sibling mods and quietly stops covering yours when it references something new; a decompile of a library with public source (here Harmony, 138k lines) is strictly worse than that source:

   ```bash
   # tools/decompile-assemblies.sh L82-L91
   SKIP_DECOMPILE=(0Harmony)

   mapfile -t NAMES < <(find "$DLL_DIR" -maxdepth 1 -name '*.dll' -printf '%f\n' | sed 's/\.dll$//' | sort)
   for skip in "${SKIP_DECOMPILE[@]}"; do
       for i in "${!NAMES[@]}"; do
           [[ ${NAMES[$i]} == "$skip" ]] && unset 'NAMES[$i]'
       done
   done
   NAMES=("${NAMES[@]}")
   [[ ${#NAMES[@]} -gt 0 ]] || { echo "error: no assemblies in $DLL_DIR" >&2; exit 1; }
   ```

4. **Wipe each output folder first, pass `-r`, and work around known decompiler failures.** Without the wipe, a type deleted by the update lingers and the diff shows the removal as nothing. ILSpy 10.1 threw on one KSA method and in project mode one unhandled method aborts the whole assembly, taking `KSA.dll` with it; disabling the stack-allocation initialiser pattern fixes that and is applied to every assembly so the corpus stays consistent:

   ```bash
   # tools/decompile-assemblies.sh L98-L126
   for name in "${NAMES[@]}"; do
       dll="$DLL_DIR/$name.dll"
       [[ -f "$dll" ]] || { echo "  skip $name (not in current/dll)"; continue; }

       out="$SRC/$name"
       # Wipe first: without it a type deleted by a KSA update lingers as a stale file and the diff
       # shows the removal as nothing at all.
       rm -rf "$out"
       mkdir -p "$out"

       # -r points the decompiler at the sibling assemblies so cross-assembly types resolve to real
       # names instead of being emitted as unresolved references.
       #
       # StackAllocInitializers=false is not a preference. ILSpy 10.1 throws "given Block is
       # invalid!" on KSA.PartModelRenderer.DepthData.CreateCsmPipeline, and in project mode one
       # unhandled method aborts the entire assembly - silently taking KSA.dll, the only one that
       # really matters, with it. Disabling the pattern emits that method as plain IL-faithful C#
       # and the other 1321 files come out fine. Applied to every assembly rather than just KSA so
       # the corpus stays consistent to diff.
       if ! err="$(ilspycmd -p -o "$out" -r "$DLL_DIR" -ds StackAllocInitializers=false "$dll" 2>&1 >/dev/null)"; then
           echo "  FAILED $name" >&2
           printf '%s\n' "$err" | head -3 | sed 's/^/      /' >&2
           failures=$((failures + 1))
           continue
       fi
       files=$(find "$out" -name '*.cs' | wc -l)
       lines=$(find "$out" -name '*.cs' -exec cat {} + 2>/dev/null | wc -l)
       printf '  %-28s %5s files  %8s lines\n' "$name" "$files" "$lines"
   done
   ```

5. **Fail loudly on a partial corpus.** A missing assembly's changes would be invisible in the next diff, and nothing about the output says so:

   ```bash
   # tools/decompile-assemblies.sh L165-L172
   # A partial corpus is worse than none: the missing assembly's changes are invisible in the next
   # diff, and nothing about the output says so.
   if (( failures )); then
       echo >&2
       echo "error: $failures assembl$( (( failures == 1 )) && echo y || echo ies) failed to decompile." >&2
       echo "       Do not commit this corpus - the next upgrade diff would silently omit them." >&2
       exit 1
   fi
   ```

6. **Commit binaries and sources together** in the private repository (`current/dll`, `current/src`, and a build stamp). The diff between two such commits is the upgrade report.

7. **Narrow the diff to your surface.** `ksa-api-diff.sh` reads the committed surface list and answers two questions that fail differently. Missing members: a member in the list that no longer appears in its type's file (mechanical, and the same set the compiler would shout about, but as a list before building). Changed files: the decompiled files defining the types the mod uses that the update touched, which is the part the compiler cannot give you:

   ```bash
   # tools/ksa-api-diff.sh L15-L22
   # Two questions, because they fail differently:
   #
   #   1. Missing members - a member in the surface that no longer appears anywhere in its
   #      assembly. Mechanical, precise, and the same set the compiler would shout about, except
   #      you get it as a list before building rather than as cascading errors after.
   #   2. Changed files - the decompiled files defining the types the mod uses, that this update
   #      touched. This is the one the compiler cannot give you: a member that kept its name and
   #      signature and changed its behaviour compiles clean and is wrong at runtime. Read these.
   ```

8. **Handle properties and moves in the member check.** Properties reach IL as `get_X`/`set_X` but the decompiled C# says `public T X { get; set; }`; strip the prefix or every property reports as missing. Look in the type's own file first (a caller elsewhere must not keep a deleted member looking alive), and report `MOVED` when it survives elsewhere in the assembly:

   ```bash
   # tools/ksa-api-diff.sh L92-L120
               # Properties and events reach IL as get_X/set_X/add_X/remove_X, but the decompiled
               # C# says `public T X { get; set; }` - the accessor name appears nowhere. Searching
               # for it reports every property this mod uses as missing.
               case "$name" in
                   get_*|set_*)       name="${name#???_}" ;;
                   add_*)             name="${name#add_}" ;;
                   remove_*)          name="${name#remove_}" ;;
               esac

               # Look in the type's own file first. ILSpy writes each type to <Namespace>/<Type>.cs,
               # so that is where a declaration is; searching the whole assembly instead lets an
               # unrelated *caller* elsewhere keep a deleted member looking alive. Fall back to the
               # assembly only when the file cannot be located, so inherited members on a type whose
               # declaration is never seen do not report as missing.
               local leaf="${type##*.}"; leaf="${leaf%%+*}"
               local file
               file="$(find "$dir" -name "${leaf}.cs" -print -quit 2>/dev/null || true)"

               if [[ -n "$file" ]]; then
                   grep -qF -- "$name" "$file" && continue
                   # Declared on a base type, or moved: still reachable, but worth a look.
                   if grep -rqlF --include='*.cs' -- "$name" "$dir" 2>/dev/null; then
                       echo "  MOVED $type.$name — no longer declared in $(basename "$file")"
                       found=1
                       continue
                   fi
               elif grep -rqlF --include='*.cs' -- "$name" "$dir" 2>/dev/null; then
                   continue
               fi
   ```

9. **List changed files that define your types.** ILSpy writes `Namespace/Type.cs`, so a leaf-name match against `git diff --name-only` finds them:

   ```bash
   # tools/ksa-api-diff.sh L145-L172
       changed="$(git -C "$TARGET" diff --name-only "$ref" -- current/src || true)"
       if [[ -z "$changed" ]]; then
           echo "  no decompiled sources changed"
           return 0
       fi

       local hits=0
       for type in "${!TYPE_ASM[@]}"; do
           # ILSpy writes Namespace/Type.cs; a nested type lives in its declaring type's file.
           local leaf="${type##*.}"; leaf="${leaf%%+*}"
           local match
           match="$(printf '%s\n' "$changed" | grep -E "/${leaf}\.cs$" || true)"
           [[ -n "$match" ]] || continue
           echo "  $type"
           printf '%s\n' "$match" | sed 's/^/      /'
           hits=$((hits + 1))
       done

       echo
       if (( hits )); then
           echo "  $hits of our types have changed definitions. Read them:"
           echo "    git -C $TARGET diff $ref -- current/src/<assembly>/<path>"
           echo
           echo "  A member that kept its name and signature can still have changed meaning."
           echo "  That is what these files are for; the compiler will not tell you."
       else
           echo "  none of the types this mod uses were touched"
       fi
   ```

## Complete example

| File | Role |
|---|---|
| [tools/decompile-assemblies.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/decompile-assemblies.sh) | Deterministic decompile into the private corpus |
| [tools/ksa-api-diff.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/ksa-api-diff.sh) | Missing members and touched files for an update |
| [tools/api-surface.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/api-surface.sh) | Generates the surface list the diff reads |
| [tools/env.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/env.sh) | Puts a .NET SDK on the path for the tools |

## Variations and gotchas

- **Private repository only.** The script guards against writing into the public repo, including through a symlink or a nested checkout.
- **Skip list is a choice.** `SKIP_DECOMPILE` keeps churn out of the diff; add to it only for libraries whose source is public.
- **Run the diff both ways.** `ksa-api-diff.sh <repo> [ref]` takes any ref, so you can diff against the build you last validated rather than the previous commit (the baseline is the last validated build, not whatever is adjacent).
- **Game updates.** This is the update tool; the procedure that uses it is in [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md).

## See also

- [Overview](overview.md); [approach 1](decompile-the-game-for-reference-1.md)
- [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md), [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md)
- Mod features: [KSArmory API surface tracking](../../../../mods/ksarmory/features/api-surface-tracking.md), [KSACars API surface pinning and diff](../../../../mods/ksacars/features/api-surface-pinning-and-diff.md), [KSAGolf API surface and assembly lock](../../../../mods/ksagolf/features/api-surface-and-assembly-lock.md)
