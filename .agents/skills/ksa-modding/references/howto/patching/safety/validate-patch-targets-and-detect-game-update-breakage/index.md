# Game Feature

* [Validating patch targets and detecting game-update breakage](overview.md) - How KSA mods resolve private game members in one validated place, let each feature refuse to load when a member moved, and make signature drift visible at build time by pinning public patch targets

# How-To

* [Feature-tagged handle registry with a Validate per feature](validate-patch-targets-and-detect-game-update-breakage-1.md) - Resolve every private game member once in one static class, tag each handle with the features that use it, validate per feature at load and skip a feature whose handle did not resolve while the others still load
* [Non-throwing static constructor with fatal and degraded lists](validate-patch-targets-and-detect-game-update-breakage-2.md) - Parts Now resolves its private ModLibrary registries in one static constructor that never throws, records each miss as a readable sentence in a fatal or degraded list and gates its Load buttons on the combined health
* [Pin patched members with a never-called method that really calls them](validate-patch-targets-and-detect-game-update-breakage-3.md) - Make a signature change in a public patch target a build error by adding a never-called method that calls it, because a call emits a member reference in the assembly and nameof does not; private targets cannot be pinned this way
