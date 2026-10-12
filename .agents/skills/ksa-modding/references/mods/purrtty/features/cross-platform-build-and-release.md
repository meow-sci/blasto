---
type: Mod Feature
title: One-zip cross-platform build, deploy and release pipeline
description: Resolve KSA reference assemblies from an env var or a sibling checkout with a fail-fast check, wipe-and-copy an allow-listed payload plus all native libraries into the mod folder on every build, and test on three OSes before stamping and publishing a release zip
mod: purrtty
category: testing-and-tooling
techniques: [native-interop]
hooks:
  - msbuild target CopyCustomContent (AfterTargets=AfterBuild)
  - github-actions .github/workflows/release.yml
lifecycle: [event:build]
game_types: []
tags: [msbuild, csproj, deployment, ci, github-actions, release, cross-platform]
---

purrTTY builds and deploys with plain `dotnet build`: the GameMod project compiles against KSA's
reference assemblies, then rewrites the `purrTTY/` folder in the KSA mods directory with exactly the
files the mod needs, including native libraries for Windows, Linux and macOS. CI runs the full test
suite on all three operating systems and publishes a single zip that works everywhere.

## How it works

**Finding the game assemblies** (`Directory.Build.props`): `KSAFolder` resolves to the first of
`KSA_DLL_DIR` (env or `-p:`), a `ksa-game-assemblies` checkout next to the repo
(`current/dll/`), the Windows install path, or a per-user default. Projects that need game DLLs set
`RequiresKSAAssemblies=true`; a `ValidateKSAAssemblies` target runs before reference resolution and
fails with one actionable error if `KSA.dll` is missing, instead of hundreds of CS0246 errors.
Game DLL references use `Private=false` so they never reach the output.[^build-props]

**Deploy target** (`CopyCustomContent` in `purrTTY.GameMod.csproj`, after every build):

1. `RemoveDir` then `MakeDir` the destination (`$(SelectedDistModDir)purrTTY\`, overridable with
   `PURRTTY_DIST_DIR`), so stale DLLs from removed projects never linger in the mod's load context.
2. Copy `mod.toml`, the `.deps.json`, every `purrTTY.*.dll` by glob, and an explicit list of
   third-party DLLs (`Ghostty.Vt`, `ModMenu.Attributes`, `Tomlyn`,
   `Microsoft.Extensions.Logging.Abstractions`, `StbImageSharp`).
3. Deliberately do not ship `0Harmony.dll` or `StarMap.API.dll`; the loader provides them
   (`PrivateAssets=all` on the package references).
4. Copy whichever of `libghostty-vt.dylib`, `libghostty-vt.so`, `ghostty-vt.dll` exist in the
   output; the vendored binding copies all three on every build, so a build on any OS produces the
   same dist.[^vt-csproj]
5. Copy `LICENSE`, `THIRD-PARTY-NOTICES.md`, `third-party-licenses/`, `TerminalFonts/`, `TerminalThemes/`.[^gamemod-csproj]

**CI** (`.github/workflows/release.yml`):[^release-yml]

| Job | What |
|---|---|
| `test` (matrix `ubuntu-latest`, `windows-latest`, `macos-14`) | Checks out the private assemblies repo with a read-only PAT into `.tmp/`, sets `KSA_DLL_DIR`, runs `dotnet test purrtty.slnx -c Release` with TRX output, publishes per-OS check runs |
| `build` (ubuntu, needs `test`) | Computes version and tag from the branch (`main` -> `tip-<UTC stamp>` prerelease, `release/<v>` -> `v<v>`), validates them against a strict pattern and passes them via `env:`, stamps `version = "..."` in `mod.toml` with `sed`, builds with `PURRTTY_DIST_DIR`, zips `purrTTY/`, publishes with `gh release` (re-push of a release branch deletes and recreates) |

Concurrency cancels superseded runs except on `release/*` refs, where a cancel between delete and
create would leave the release missing.

## Lifecycle and timing

Build-time only. The output layout (mod folder with `mod.toml` and `EntryAssembly =
"purrTTY.GameMod"`) is what StarMap scans at game start; native libraries must sit beside the DLLs
because the resolver looks there (see [native library resolver](./per-platform-native-library-resolver.md)).

## Key code

| Path | Role |
|---|---|
| [Directory.Build.props](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/Directory.Build.props) | Framework settings, `KSAFolder` and dist resolution, fail-fast target |
| [purrTTY.GameMod/purrTTY.GameMod.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/purrTTY.GameMod.csproj#L22-L78) | `CopyCustomContent` |
| [vendor/Ghostty.Vt/Ghostty.Vt.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/Ghostty.Vt.csproj) | Flat copy of all native libraries |
| [.github/workflows/release.yml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.github/workflows/release.yml) | Test matrix and release job |
| [purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml) | Manifest stamped by CI |
| [docs/build-and-test.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/build-and-test.md) | Human documentation of the above |

## Snippets

```xml
<!-- purrTTY.GameMod/purrTTY.GameMod.csproj L22-L53 -->
    <!-- Copies files for mod deployment -->
    <Target Name="CopyCustomContent" AfterTargets="AfterBuild">

        <!-- Wipe the destination first so a redeploy never leaves stale DLLs from a
             previous version in the game's mod ALC (deleted projects, renamed deps). -->
        <RemoveDir Directories="$(DistDir)"/>
        <MakeDir Directories="$(DistDir)"/>

        <Copy SourceFiles="$(TargetDir)mod.toml" DestinationFolder="$(DistDir)" Condition="Exists('$(TargetDir)mod.toml')"/>
        <Copy SourceFiles="$(ProjectDepsFilePath)" DestinationFolder="$(DistDir)" Condition="Exists('$(ProjectDepsFilePath)')"/>

        <!-- Managed payload: every purrTTY assembly by glob (a hand-maintained list is how
             omissions happen) plus the explicit non-purrTTY dependencies we ship. NOT shipped
             on purpose: 0Harmony.dll + StarMap.API.dll (supplied by the StarMap loader) and
             the KSA reference assemblies (Private=false, never in the output anyway). -->
        <ItemGroup>
            <ModManagedDll Include="$(TargetDir)purrTTY.*.dll"/>
            <ModManagedDll Include="$(TargetDir)Ghostty.Vt.dll"/>
            <ModManagedDll Include="$(TargetDir)ModMenu.Attributes.dll"/>
            <ModManagedDll Include="$(TargetDir)Tomlyn.dll"/>
            <ModManagedDll Include="$(TargetDir)Microsoft.Extensions.Logging.Abstractions.dll"/>
            <!-- Managed PNG/JPEG decoder for kitty-graphics image payloads. -->
            <ModManagedDll Include="$(TargetDir)StbImageSharp.dll"/>
        </ItemGroup>
        <Copy SourceFiles="@(ModManagedDll)" DestinationFolder="$(DistDir)"/>

        <!-- Native libghostty-vt: copy whichever platform binaries the build produced,
             so the NativeLibraryResolver finds the library beside the mod DLL. -->
        <ItemGroup>
            <GhosttyNative Include="$(TargetDir)libghostty-vt.dylib" Condition="Exists('$(TargetDir)libghostty-vt.dylib')" />
            <GhosttyNative Include="$(TargetDir)libghostty-vt.so" Condition="Exists('$(TargetDir)libghostty-vt.so')" />
            <GhosttyNative Include="$(TargetDir)ghostty-vt.dll" Condition="Exists('$(TargetDir)ghostty-vt.dll')" />
```

```xml
<!-- Directory.Build.props L42-L46 -->
    <PropertyGroup>
        <KSAFolder Condition="'$(KSAFolder)' == '' AND '$(KSA_DLL_DIR)' != ''">$([MSBuild]::EnsureTrailingSlash('$(KSA_DLL_DIR)'))</KSAFolder>
        <KSAFolder Condition="'$(KSAFolder)' == '' AND Exists('$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/')">$(MSBuildThisFileDirectory)../ksa-game-assemblies/current/dll/</KSAFolder>
        <KSAFolder Condition="'$(KSAFolder)' == '' AND '$(OS)' == 'Windows_NT'">C:\Program Files\Kitten Space Agency\</KSAFolder>
        <KSAFolder Condition="'$(KSAFolder)' == ''">$(HOME)/repos/meow-sci/ksa-game-assemblies/current/dll/</KSAFolder>
```

```xml
<!-- Directory.Build.props L62-L65 -->
    <Target Name="ValidateKSAAssemblies" BeforeTargets="ResolveAssemblyReferences"
            Condition="'$(RequiresKSAAssemblies)' == 'true' AND !Exists('$(KSAFolder)KSA.dll')">
        <Error Text="KSA reference assemblies not found: '$(KSAFolder)KSA.dll' does not exist. Set the KSA_DLL_DIR environment variable (or pass -p:KSA_DLL_DIR=...), or clone meow-sci/ksa-game-assemblies next to this repo (DLLs under current/dll/). Resolution order is documented in Directory.Build.props."/>
    </Target>
```

## Game APIs touched

None at runtime. Build references: `KSA.dll`, `Brutal.*.dll`, `Planet.Render.Core.dll` from the game
assemblies folder.

## Pitfalls and version notes

- Wipe the destination before copying; overwriting leaves old assemblies that StarMap's context may
  still resolve.
- Do not ship `0Harmony.dll` or `StarMap.API.dll`; a second copy risks type-identity splits.
- An allow-list (purrTTY) needs updating for every new dependency; gatOS uses a glob with a deny-list
  instead (comment in gatOS's csproj).
- Game DLLs are proprietary; CI pulls them from a private repo with a scoped token rather than
  committing them.
- Branch-derived strings reach shell steps through `env:` and are validated; never interpolate
  `${{ }}` directly into `run:` in a job with write permissions.

## See also

- [Per-platform native library resolver](./per-platform-native-library-resolver.md)
- [Vendored libghostty-vt binding](./vendored-libghostty-vt-binding.md)
- [Headless test suites](./headless-test-suites.md)
- [KSA upgrade coupling review skill](./ksa-upgrade-coupling-review-skill.md)
- [purrTTY overview](../overview.md)
- [Publish rolling releases from a multi-assembly mod with a stamped version and a validated folder](../../../howto/getting-started/build/package-and-publish-a-mod-release/package-and-publish-a-mod-release-5.md) - how-to article demonstrating this feature

[^build-props]: Directory.Build.props
[^vt-csproj]: vendor/Ghostty.Vt/Ghostty.Vt.csproj
[^gamemod-csproj]: purrTTY.GameMod/purrTTY.GameMod.csproj
[^release-yml]: .github/workflows/release.yml
