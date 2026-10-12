---
type: Mod Feature
title: Local decompiled game source refreshed with ilspycmd
description: A PowerShell script decompiles selected game assemblies with ilspycmd into a gitignored decompiled folder and records the game version, and the repository's assistant instructions point code assistants at that tree instead of running ILSpy per lookup
mod: compendium
category: testing-and-tooling
techniques: [decompiling, agent-instructions]
hooks: []
lifecycle: []
game_types: []
tags: [tooling, decompile, ilspy, powershell, ai-assistants]
---

Compendium's code touches many private game members (navball, gauges, flight computer), and each game
update can move them. The repository keeps a searchable copy of the decompiled game next to the mod
source, refreshed by one command after every game update, and tells AI code assistants to search it
rather than decompiling ad hoc.[^cmp-copilot-instructions-md]

## How it works

1. **Script.** `tools/Refresh-Decompiled.ps1` takes the game folder (default
   `C:\Program Files\Kitten Space Agency`) and a list of assemblies (default `KSA`, `Brutal.ImGui`,
   `Brutal.Core.Numerics`, `Planet.Core`). It installs `ilspycmd` as a global dotnet tool if missing,
   then for each DLL deletes `decompiled/<name>`, runs `ilspycmd -p -o <dir> -r <gameDir> <dll>`
   (a compilable project with references resolved from the game folder) and fails on a non-zero exit
   code ([tools/Refresh-Decompiled.ps1](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/tools/Refresh-Decompiled.ps1#L1-L31)).[^cmp-refresh-decompiled-ps1]
2. **Version stamp.** It writes `decompiled/VERSION.txt` with the `ProductVersion` of `KSA.dll` and the
   date, so a stale tree is easy to detect ([tools/Refresh-Decompiled.ps1](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/tools/Refresh-Decompiled.ps1#L33-L35)).
3. **Kept out of git.** `.gitignore` excludes `decompiled/` (and `.decompiled`)
   ([.gitignore](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.gitignore#L45-L46)).[^cmp-gitignore]
4. **Assistant instructions.** `.github/copilot-instructions.md` tells the assistant that the decompiled
   source is in `decompiled/`, to search it with normal workspace search, to regenerate it when
   `VERSION.txt` does not match `KSA.dll`, where the game's content XML lives, and summarizes the
   multi-star-system model (all systems in one `Universe.CurrentSystem`, one `IIndependentRoot` per
   system in `Universe.Roots`, `WorldSun` follows the camera). The same file records the JSON catalog
   rules ([.github/copilot-instructions.md](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.github/copilot-instructions.md#L13-L19)).

## Lifecycle and timing

Development time only; nothing here runs in the game.

## Key code

| Path | Role |
|---|---|
| [tools/Refresh-Decompiled.ps1](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/tools/Refresh-Decompiled.ps1) | Decompile and stamp |
| [.github/copilot-instructions.md](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.github/copilot-instructions.md) | Assistant guidance for the decompiled tree and the JSON data |
| [.gitignore](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/.gitignore#L45-L46) | Keep the decompiled tree out of the repository |

## Snippets

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

## Game APIs touched

None at runtime. The script reads `KSA.dll`, `Brutal.ImGui.dll`, `Brutal.Core.Numerics.dll` and
`Planet.Core.dll` from the game folder.

## Pitfalls and version notes

- **Windows paths.** The default game folder is the Windows install path; pass `-GameDir` elsewhere.
- **Not every referenced assembly is decompiled by default.** The csproj also references
  `Brutal.Vulkan`, `Brutal.Vulkan.Abstractions`, `Brutal.Texture`, `Brutal.Texture.Abstractions`,
  `Brutal.Stb` and `Planet.Render.Core` (used for the logo texture)
  ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L122-L149)); add them with `-Assemblies` when working on that code.[^cmp-compendium-csproj]
- **Version stamp is informational.** Nothing checks `VERSION.txt` automatically; the instructions
  rely on the assistant or developer to compare it.

## See also

- [Star systems and the body tree](star-systems-and-body-tree.md)
- [Cross-mod JSON catalog](cross-mod-json-catalog.md)
- [Refresh a gitignored decompiled tree with ilspycmd, stamp its version and point your assistant at it](../../../howto/testing-and-tooling/game-updates/decompile-the-game-for-reference/decompile-the-game-for-reference-1.md) - how-to article demonstrating this feature

[^cmp-copilot-instructions-md]: Compendium: .github/copilot-instructions.md
[^cmp-refresh-decompiled-ps1]: Compendium: tools/Refresh-Decompiled.ps1
[^cmp-gitignore]: Compendium: .gitignore
[^cmp-compendium-csproj]: Compendium: src/Compendium.csproj
