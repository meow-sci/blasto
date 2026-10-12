# Game Feature

* [Generate mod.toml and StarMap dependencies from the project file](overview.md) - Why generating mod.toml from MSBuild keeps version, entry assembly, asset list and dependencies in step with the code, and the shared targets that do it

# How-To

* [Generate mod.toml and StarMap dependencies from the project file](generate-mod-toml-from-msbuild.md) - Declare version, entry assembly, asset lists and StarMap dependencies as csproj properties and items, and have a shared CopyOutput.targets write mod.toml on every build so it cannot drift from the code
