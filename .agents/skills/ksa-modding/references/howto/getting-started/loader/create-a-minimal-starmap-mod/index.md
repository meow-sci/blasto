# Game Feature

* [Create a minimal StarMap mod](overview.md) - What KSA and StarMap require of a code mod folder, and two ways to stand one up - by hand from the discovery rules, or from a repo template that grows into a library plus entry layout

# How-To

* [Build a minimal StarMap mod by hand from the discovery rules](create-a-minimal-starmap-mod-1.md) - Create the smallest folder StarMap will load - a mod.toml with only a name, one StarMapMod class, a csproj with Private=false game references - and see it log
* [Scaffold a mod with mkmod.ts and grow it into a library hosted by a supermod](create-a-minimal-starmap-mod-2.md) - Clone unscience's fixme-mod-name template pair with one script, then keep feature code in a host-agnostic ISubmod library that runs standalone or inside an aggregating mod
