# Game Feature

* [Understand when and in which order KSA loads mod XML](overview.md) - The mod.toml keys feed separate Mod loader methods that run in a fixed order inside the Program constructor, which decides where patches, redirects, StarMap hooks and runtime registrations can see the data

# How-To

* [Find out when KSA reads each mod.toml key and where hooks can see the data](mod-xml-load-order.md) - Per-phase table of the mod.toml keys, the Mod loader each feeds, the ModLibrary call that runs it inside the Program constructor, and the StarMap hook or patch point that sees the result
