# Game Feature

* [Refusing a patch and rolling back when the game changes](overview.md) - How KSA mods treat a changed game method as a normal outcome by validating targets, throwing from transpilers, rolling back by Harmony id and publishing success flags, and when an anchor failure should stop the launch instead

# How-To

* [Let an anchor failure stop the launch (and why that can be right)](refuse-a-patch-and-roll-back-when-the-game-changes-3.md) - ShaderExtensions applies its RenderGame transpiler with PatchAll inside StarMapBeforeMain and lets a missing anchor abort startup, because StarMap does not catch it; compare that with rolling back and note what a shifted local index does not trigger
* [One Harmony owner per feature with TryApply, validated handles and runtime flags](refuse-a-patch-and-roll-back-when-the-game-changes-2.md) - Give each feature its own Harmony id, validate its reflection handles before patching, roll a failed block back by id, gate dependent features on success and set the runtime flags that shared hooks read from the TryApply result
* [Throw on a bad match, UnpatchAll(own id), publish the instance on success](refuse-a-patch-and-roll-back-when-the-game-changes-1.md) - Treat a changed game method as a normal outcome: validate lookups, make the transpiler throw unless exactly one call matched, catch around Patch, unpatch by your own Harmony id and publish the instance only on success so the UI stays honest
