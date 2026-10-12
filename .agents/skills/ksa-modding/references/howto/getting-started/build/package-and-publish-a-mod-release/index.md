# Game Feature

* [Package and publish a mod release (zip, CI publishing, installer)](overview.md) - What a mod release must contain, six ways the corpus produces and verifies it - Release targets, payload verification, data and licences, CI publishing, rolling channels and an installer

# How-To

* [Publish from CI with a workflow that reads the tested game version from Mod.cs](package-and-publish-a-mod-release-4.md) - Make the TestedGameVersion constant the single source for the code guard, the release notes and the SpaceDock game-version field by extracting it with grep in the release workflow
* [Publish rolling releases from a multi-assembly mod with a stamped version and a validated folder](package-and-publish-a-mod-release-5.md) - Stamp a version into mod.toml in CI, build the whole solution, validate the single-mod dist folder with a script, zip it and publish a tip or release channel with retention - for unscience and purrTTY
* [Release RC builds, a NuGet API package and a Windows installer for a launcher-style tool](package-and-publish-a-mod-release-6.md) - StarMap's own release pipeline - RC prereleases from dev, label-driven versions on main, a framework-dependent win-x64 zip without pdb and xml, StarMap.API pushed to NuGet when changed, and an Inno Setup installer that writes StarMapConfig.json
* [Ship data files and third-party licences with the mod](package-and-publish-a-mod-release-3.md) - Copy data folders and licence texts into the mod with TargetPath items, add a NOTICE for GPL and CC BY-SA components, and avoid a hand-committed release zip that goes stale
* [Verify the release zip against the declared payload file by file](package-and-publish-a-mod-release-2.md) - Unzip the finished archive and diff it against the payload list in both directions, so a missing file or an undeclared game or loader assembly fails the build or the package script
* [Zip the deployed folder with a Release MSBuild target](package-and-publish-a-mod-release-1.md) - Add a Release-only target that stages exactly the files the deploy copies - DLL and mod.toml - and zips them into a folder users extract into their mods directory
