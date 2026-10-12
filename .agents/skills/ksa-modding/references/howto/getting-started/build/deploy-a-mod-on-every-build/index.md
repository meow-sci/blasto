# Game Feature

* [Deploy a mod on every build and debug it from an IDE](overview.md) - How a build leaves a runnable mod in the KSA mods folder, five deploy designs from OutputPath to a shared payload list, and the launch profile that starts StarMap.exe under a debugger

# How-To

* [Add a plain post-build copy with an overridable KSAGameDir](deploy-a-mod-on-every-build-4.md) - Lowest-ceremony deploy - an AfterBuild target that copies the DLL, deps.json, manifest and data into the mods folder, with the game directory overridable from the environment or an untracked user props file
* [Generate a launchSettings profile so Rider or Visual Studio runs StarMap and hits mod breakpoints](deploy-a-mod-on-every-build-5.md) - Write a StarMap.exe launch profile with the right working directory on first build, so F5 builds, deploys and starts the game under the debugger in any fresh checkout or worktree
* [Import a shared ModDeploy.targets that diagnoses a locked DLL](deploy-a-mod-on-every-build-1.md) - Copy the mod DLL and mod.toml into the KSA mods folder after every build with a single-attempt copy that names the running game when the file is locked, plus a deploy stamp
* [Point OutputPath straight at the mods folder](deploy-a-mod-on-every-build-2.md) - The simplest possible deploy - set OutputPath to the user mods folder and mark content CopyToOutputDirectory, so every build is already the install; no target needed
* [Share one payload list between the deploy copy and the release zip](deploy-a-mod-on-every-build-3.md) - Declare every shipped file once as ModPayloadFile items - per-RID natives, satellite assemblies, licences - and let both the dev deploy and the packaged zip read the list, with a running-game check
