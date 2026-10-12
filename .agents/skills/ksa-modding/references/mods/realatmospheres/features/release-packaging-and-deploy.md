---
type: Mod Feature
title: Release packaging and deploy scripts
description: Two PowerShell scripts sharing one asset list build the mod and either install it into the active game instance or package a release zip whose entries extract correctly on Linux
mod: realatmospheres
category: testing-and-tooling
techniques: [build-scripts, packaging]
hooks: []
lifecycle: []
game_types: []
tags: [tooling, packaging, deploy, powershell, borea, manifest, zip, release]
---

`deploy.ps1` is the development loop and `package.ps1` makes the player archive. Both read
`release-files.txt`, so the local install always contains exactly what players get.[^ra-deploy-ps1]

## How it works

- **Find the game's data folder.** Both scripts prefer the active Borea instance
  (`%LOCALAPPDATA%\Borea\active-instance.toml` -> `Instances\<id>`) and fall back to
  `Documents\My Games\Kitten Space Agency` ([deploy.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/deploy.ps1#L11-L25)). The `.csproj` does the
  same for the StarMap folder it compiles against: Borea's loader copy first, then a standalone
  `StarMapLauncher-0.4.5` folder ([RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L13-L16)).
- **Deploy.** `dotnet build -c Release`, copy the DLL, `mod.toml`, README, LICENSE and CREDITS into
  `mods\RealAtmospheres`, copy each listed asset (failing if one is missing), delete assets in the
  install that are no longer listed, and leave `ShadowContent\` alone because the mod regenerates it
  ([deploy.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/deploy.ps1#L27-L50)).
- **Enable for development.** KSA adds new mod folders to its `manifest.toml` disabled; the script
  appends `[[mods]] id = "RealAtmospheres" enabled = true` if the id is not listed yet, and leaves an
  existing entry as the user set it ([deploy.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/deploy.ps1#L52-L59)).
- **Package.** The game build in the archive name defaults to `lastVersion` from the game's
  `settings.toml`, i.e. the build the developer last ran ([package.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/package.ps1#L31-L39)). The
  zip root is a `RealAtmospheres\` folder because KSA takes the folder name as the mod id; StarMap.API
  and 0Harmony are not included ([package.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/package.ps1#L1-L8)).
- **Cross-platform archive.** Entries are added one by one with forward slashes, because Windows
  PowerShell 5.1's `Compress-Archive` and `ZipFile.CreateFromDirectory` write backslashes that Linux
  extracts as file names; the `.sha256` is written as a single LF-terminated line so `sha256sum -c`
  works ([package.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/package.ps1#L64-L83)).

## Lifecycle and timing

None in the game (build time). The deployed folder is what StarMap reads at the next launch:
`mod.toml` names `EntryAssembly = "RealAtmospheres"` ([mod.toml](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/mod.toml)).

## Key code

| Path | Role |
|---|---|
| [deploy.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/deploy.ps1) | Build, install, prune, enable |
| [package.ps1](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/package.ps1) | Build, stage, zip, hash |
| [release-files.txt](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/release-files.txt) | Shared asset list |
| [RealAtmospheres.csproj](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/RealAtmospheres.csproj#L3-L17) | Output settings and StarMap folder detection |

## Snippets

```powershell
# package.ps1 L64-L83
$zip = Join-Path $dist ("RealAtmospheres-v{0}-ksa{1}.zip" -f $Version, $GameBuild.TrimStart("v"))
if (Test-Path $zip) { Remove-Item $zip -Force }
# Each entry is named here, with forward slashes, as the ZIP format requires. Under Windows PowerShell 5.1 both
# Compress-Archive and ZipFile.CreateFromDirectory write backslashes, which Linux extracts as part of the file
# name instead of as folders (KSA ships a Linux build).
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$base = $dist.TrimEnd('\') + '\'
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem $stage -Recurse -File | Sort-Object FullName)) {
        $entry = $file.FullName.Substring($base.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
# One LF-terminated line: with Set-Content's CRLF, `sha256sum -c` reads the name with a trailing CR.
[System.IO.File]::WriteAllText($zip + ".sha256", ("{0}  {1}`n" -f $hash, (Split-Path $zip -Leaf)))
```

```powershell
# deploy.ps1 L52-L59
# The game auto-adds new mod folders to its local manifest DISABLED; for the dev loop, register it
# enabled if it isn't listed yet (an existing entry, enabled or not, is left as the user set it).
$manifest = Join-Path $gameDir "manifest.toml"
if (-not (Test-Path $manifest)) { throw "local manifest not found: $manifest (run the game once first)" }
if ((Get-Content $manifest -Raw) -notmatch 'id = "RealAtmospheres"') {
    Add-Content $manifest "`n[[mods]]`nid = `"RealAtmospheres`"`nenabled = true" -Encoding utf8
    Write-Host "added RealAtmospheres to $manifest"
}
```

## Game APIs touched

| Item | Source | Used for |
|---|---|---|
| Mod id is the folder name | [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L1-L7) | Why the zip root folder is named `RealAtmospheres` |

## Pitfalls and version notes

- **Windows-only scripts.** PowerShell, `%LOCALAPPDATA%` and `Documents` paths; there is no Linux or
  macOS equivalent in the repository.
- **The tested build in the file name is whatever was run last** unless `-GameBuild` is passed.
- **No CI.** Packaging is manual; the README asks the user to set `StarMapDir` when StarMap is not in
  one of the two default places ([README.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/README.md#L106-L108)).

## See also

- [Reflection-only mod build](reflection-only-mod-build.md)
- [Texture and phase asset scripts](texture-and-phase-asset-scripts.md)
- [Release packaging, StarMap.API NuGet and Windows installer (StarMap)](../../starmap/features/release-packaging-and-installer.md)
- [Zip the deployed folder with a Release MSBuild target](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-1.md) - how-to article demonstrating this feature

[^ra-deploy-ps1]: deploy.ps1
