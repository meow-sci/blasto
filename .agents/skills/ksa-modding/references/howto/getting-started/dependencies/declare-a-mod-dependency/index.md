# Game Feature

* [Declare a dependency on another StarMap mod, required or optional](overview.md) - How StarMap orders and parks mods by their ModDependencies, what Optional and ImportedAssemblies change, and how to integrate with or stand down from a mod you do not declare

# How-To

* [Declare a required dependency with ImportedAssemblies](declare-a-mod-dependency-1.md) - Add a [[StarMap.ModDependencies]] entry so StarMap loads your mod after another, shares the assembly you compile against, and parks your mod until the dependency initializes
* [Make a dependency optional and ship a vendored fallback copy of the contract](declare-a-mod-dependency-2.md) - Mark a StarMap dependency Optional = true, import its contract assemblies, vendor pinned copies for when it is absent, and detect at run time which copy loaded
* [Probe for another mod at AllModsLoaded and stand down when a conflicting one is present](declare-a-mod-dependency-3.md) - Detect an optional or superseded mod without a mod.toml dependency, by type name or by a marker registered at immediate load, then enable, skip or warn - and import its settings once
