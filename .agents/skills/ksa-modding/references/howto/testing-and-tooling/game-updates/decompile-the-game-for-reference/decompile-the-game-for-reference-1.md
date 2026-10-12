---
type: How-To
title: Refresh a gitignored decompiled tree with ilspycmd, stamp its version and point your assistant at it
description: Decompile the game assemblies you depend on into a gitignored folder with one command, write a version stamp beside it and tell tools and AI assistants to search it instead of decompiling ad hoc
category: testing-and-tooling
group: game-updates
topic: decompile-the-game-for-reference
approach: 1
difficulty: beginner
prerequisites: [dotnet-sdk, powershell]
demonstrated_by: [mods/compendium/features/decompiled-source-refresh]
techniques: [decompiling, tooling, ai-assistants]
hooks: []
lifecycle: []
game_types: []
tags: [decompile, ilspy, ilspycmd, reference, game-update, ai-assistant]
---

Outcome: a `decompiled/` folder next to your mod with a compilable C# project per game assembly, a `VERSION.txt` that says which game build it came from, and an instructions file that tells you, your IDE assistant and any AI tool to search it with ordinary workspace search. One command regenerates it after a game update.
Demonstrated by Compendium ([tools/Refresh-Decompiled.ps1](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/tools/Refresh-Decompiled.ps1#L3-L35)). Prerequisites: the .NET SDK and a game install to read the DLLs from.

## Background: how the game does it

KSA has no official modding API (the analysed mods' own notes say so), so the only authoritative description of `Program.OnFrame`, `Universe` or `Vehicle` is the compiled code. A decompiler turns it into readable C#, with the caveats that names of locals are lost and some constructs (stack-allocation initialisers, for example) can fail. The structure the output follows is ILSpy's: one file per type at `<Namespace>/<Type>.cs`, which is the layout of this corpus's game checkout (`current/decomp/KSA/Program.cs`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141) for `PrepareFrame`) and the one the second approach diffs between builds. The build number lives in the assemblies and in the changelog ([current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json#L1-L5)).
The decompiled code is the game developer's copyrighted work: keep it out of any public repository, which is what the gitignore line is for.

## Lifecycle and timing

Development time only; nothing here runs in the game, and there is no frame position. Frame and lifecycle claims that you derive from the decompiled tree should still be checked against `Program.cs` and `Universe.cs` (see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Write a refresh script that takes the game folder and a list of assemblies.** Defaults should be the ones your code touches. The script installs `ilspycmd` as a global dotnet tool if it is missing, deletes the old output for each assembly (so deleted types do not linger), runs `ilspycmd -p -o <dir> -r <gameDir> <dll>` (`-p` makes a compilable project, `-r` resolves references from the game folder), and fails on a non-zero exit:

   ```powershell
   # tools/Refresh-Decompiled.ps1 L3-L35
   param(
       [string]$GameDir = 'C:\Program Files\Kitten Space Agency',
       [string[]]$Assemblies = @('KSA', 'Brutal.ImGui', 'Brutal.Core.Numerics', 'Planet.Core')
   )

   $ErrorActionPreference = 'Stop'
   $repoRoot = Split-Path -Parent $PSScriptRoot
   $outRoot = Join-Path $repoRoot 'decompiled'

   if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
       Write-Host 'ilspycmd not found - installing as a global dotnet tool...'
       dotnet tool install -g ilspycmd
   }

   foreach ($name in $Assemblies) {
       $dll = Join-Path $GameDir "$name.dll"
       if (-not (Test-Path $dll)) {
           Write-Warning "Skipping $name - not found at $dll"
           continue
       }

       $outDir = Join-Path $outRoot $name
       if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
       New-Item -ItemType Directory -Path $outDir | Out-Null

       Write-Host "Decompiling $name.dll -> $outDir"
       ilspycmd -p -o $outDir -r $GameDir $dll
       if ($LASTEXITCODE -ne 0) { throw "ilspycmd failed for $name" }
   }

   $version = (Get-Item (Join-Path $GameDir 'KSA.dll')).VersionInfo.ProductVersion
   Set-Content -Path (Join-Path $outRoot 'VERSION.txt') -Value "KSA $version decompiled $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
   Write-Host "Done. Game version: $version"
   ```

2. **Keep the output out of git.** Ignore the folder in `.gitignore`:

   ```
   // .gitignore L45-L46
   .decompiled
   decompiled/
   ```

3. **Tell assistants where the source is.** A short section in the project's assistant instructions file says where the decompiled source lives, to search it with normal workspace search rather than decompiling per lookup, and when to regenerate it (when `VERSION.txt` does not match `KSA.dll`):

   `.github/copilot-instructions.md L13-L18`:

   ```markdown
   ## Game source reference (use instead of ILSpy)

   - Decompiled game source lives in `decompiled/` (gitignored): `KSA`, `Brutal.ImGui`, `Brutal.Core.Numerics`, `Planet.Core`. `decompiled/VERSION.txt` records the game version it was made from.
   - Search/read those `.cs` files with normal workspace search instead of running ILSpy/ilspycmd for each lookup.
   - After a game update (or if `VERSION.txt` doesn't match `KSA.dll`'s version), regenerate with `pwsh ./tools/Refresh-Decompiled.ps1` (add assemblies via `-Assemblies`).
   - Game content XML (systems, bodies) is under `C:\Program Files\Kitten Space Agency\Content\Core\`.
   ```

4. **Run it once and after every game update:** `pwsh ./tools/Refresh-Decompiled.ps1`, adding assemblies the code needs with `-Assemblies` (the Compendium csproj also references Vulkan, texture and `Planet.Render.Core` assemblies that are not in the default list, [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L122-L149)).

5. **Search, do not read.** Grep for the member you call and read its callers; the engine's real behaviour (units, frames, who calls whom) is in the code, not in the signature. Then link back to the line when you write down what you learned.

## Complete example

| File | Role |
|---|---|
| [tools/Refresh-Decompiled.ps1](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/tools/Refresh-Decompiled.ps1) | Decompile and stamp |
| [.github/copilot-instructions.md](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.github/copilot-instructions.md) | Assistant guidance for the decompiled tree |
| [.gitignore](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.gitignore#L45-L46) | Keep the decompiled tree out of the repository |

## Variations and gotchas

- **Windows paths.** The default game folder is the Windows install path; pass `-GameDir` elsewhere.
- **The stamp is informational.** Nothing checks `VERSION.txt` automatically; the instructions rely on a person or an assistant to compare it.
- **Decompilation is not source.** Treat decompiled output as a faithful reading of the IL; names and comments are gone, and a single failing method can abort a whole assembly in project mode (see [approach 2](decompile-the-game-for-reference-2.md) for the workaround).
- **Keep the set stable.** Changing which assemblies are decompiled changes how cross-assembly types resolve, which makes a diff between two runs unreadable.
- **Game updates.** Re-run the script; then diff the old and new trees (approach 2) rather than rereading everything.

## See also

- [Overview](overview.md); [approach 2](decompile-the-game-for-reference-2.md)
- [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md), [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md), [Pin the KSA build with a hashed lock](../pin-the-game-build-with-a-hashed-lock/overview.md)
- Mod feature: [Compendium decompiled source refresh](../../../../mods/compendium/features/decompiled-source-refresh.md)
