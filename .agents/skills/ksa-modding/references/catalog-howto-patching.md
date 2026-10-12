# Patching

Generic Harmony technique for KSA mods - skipping prefixes, finalizers and scoped overrides, coexisting with other mods, worker-thread patches, writing and anchoring transpilers, swapping single calls, reaching private members and patching around AggressiveInlining, and fault-contained, refusing, validated and removable patch sets.

## Harmony

Everyday Harmony technique against KSA: skipping prefixes, finalizers, sharing a method with other mods and patching code that runs off the main thread.

### [Coexisting with other mods that patch the same method](howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/overview.md)

How Harmony orders and re-runs patches when several mods target one game method, and how KSA mods order, share and merge patches so that features and neighbouring mods do not break each other

- [Priority.First prefixes and Priority.Last finalizers around shared seams](howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-1.md) — intermediate
- [Anchors that one mod can consume (and what the next transpiler then sees)](howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md) — advanced
- [Idempotent shared installs with a registration API](howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-3.md) — intermediate
- [One shared tick with per-feature enable flags set from TryApply results](howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-4.md) — advanced
- [One worker-side postfix merging every writer](howto/patching/harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-5.md) — advanced

### [Patching game methods that run on worker and pool threads](howto/patching/harmony/patch-methods-that-run-on-background-threads/overview.md)

Which KSA methods run on vehicle workers, Bepu threads and the thread pool, what a Harmony patch may touch there, and how results and data cross between those threads and the main thread

- [Patches on planner tasks that run on pooled threads](howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-1.md) — advanced
- [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-2.md) — advanced
- [Worker-side postfix merging commands from the main thread](howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-3.md) — advanced
- [ConditionalWeakTable keyed by objects the copy shares by reference](howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-4.md) — advanced
- [Look the state up through __instance.BurnPlan in a ComputeControl prefix](howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-5.md) — advanced

### [Harmony finalizers, cleanup and scoped overrides](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/overview.md)

Why a postfix does not run when the original throws and a finalizer does, how KSA mods use finalizers to restore state and swallow failures, and how to scope a patch on a shared method to the mod's own calls

- [Restore what a prefix changed around one game call](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md) — advanced
- [Swallow an exception and finish the method's remaining work](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md) — advanced
- [Prefix with __state and a finalizer that restores an edited input](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md) — advanced
- [Prefix, postfix and finalizer as a transaction around loads](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md) — advanced
- [ThreadStatic override set in try/finally around the mod's own submission](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md) — intermediate
- [Thread-static construction context scoping a transpiler and a prefix](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md) — advanced
- [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](howto/patching/harmony/run-cleanup-or-recover-from-exceptions-with-a-finalizer/run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md) — advanced

### [Skipping Harmony prefixes](howto/patching/harmony/skip-the-original-method-with-a-prefix/overview.md)

When and how to return false from a Harmony prefix in KSA to replace, veto, rewrite or consume a call, what a skipped original still owes the game, and when a postfix is safer

- [Reproduce the original body for marked objects and return false](howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-1.md) — intermediate
- [Rewrite by-ref arguments and skip when a limit would throw](howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-2.md) — intermediate
- [Re-inject a private field before a rebuild so nothing can change it back](howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-3.md) — intermediate
- [Mouse prefix that pairs presses with releases and repairs private latches](howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-4.md) — advanced
- [Keyboard and mouse-binding gates with a held-key model](howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-5.md) — advanced

## Safety

Keeping patches survivable across game updates and other mods: refusing and rolling back, isolating failures, validating targets and removing patches cleanly.

### [Isolating patch failures and keeping patch bodies from throwing](howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)

How KSA mods localise damage when a game update breaks one Harmony target and how they write patch bodies that cannot take the game's frame loop down, from per-class application to fault counters

- [CreateClassProcessor per patch class, required versus optional](howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md) — intermediate
- [Consolidated patcher with TryApply and ordered shared seams](howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-2.md) — advanced
- [Static patch classes that look up the target, log once and stand down](howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-3.md) — intermediate
- [Reflection-only mod with parameter-name and field-type pre-checks](howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-4.md) — advanced
- [Fault counter that pulls every patch after a limit](howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-5.md) — intermediate

### [Refusing a patch and rolling back when the game changes](howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/overview.md)

How KSA mods treat a changed game method as a normal outcome by validating targets, throwing from transpilers, rolling back by Harmony id and publishing success flags, and when an anchor failure should stop the launch instead

- [Throw on a bad match, UnpatchAll(own id), publish the instance on success](howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md) — intermediate
- [One Harmony owner per feature with TryApply, validated handles and runtime flags](howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-2.md) — advanced
- [Let an anchor failure stop the launch (and why that can be right)](howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-3.md) — advanced

### [Removing Harmony patches safely at runtime and at exit](howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/overview.md)

How to remove only your own Harmony patches, from a main-thread hook, after nulling the delegates they call, and why StarMap's shutdown order means unload code usually finds nothing left to remove

- [UnpatchAll(ownId) from a main-thread hook after nulling delegates](howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-1.md) — intermediate
- [Lazy install and removal of one patch while it is in use](howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-2.md) — intermediate
- [Unpatch by patch method on a shared Harmony instance](howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-3.md) — advanced

### [Validating patch targets and detecting game-update breakage](howto/patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)

How KSA mods resolve private game members in one validated place, let each feature refuse to load when a member moved, and make signature drift visible at build time by pinning public patch targets

- [Feature-tagged handle registry with a Validate per feature](howto/patching/safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-1.md) — advanced
- [Non-throwing static constructor with fatal and degraded lists](howto/patching/safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-2.md) — intermediate
- [Pin patched members with a never-called method that really calls them](howto/patching/safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-3.md) — intermediate

## Transpilers

Changing a game method's IL: the transpiler recipe, swapping one call, finding the right call among several and inserting at an anchor.

### [Identifying the right call in a method that makes several similar calls](howto/patching/transpilers/identify-a-call-by-its-argument-il/overview.md)

How KSA transpilers pick one instruction among many similar ones by what surrounds it: the loads that feed its arguments, a unique neighbouring call, a UI label literal, or the instruction that consumes its result

- [Scan backwards through the argument loads to the previous call](howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-1.md) — intermediate
- [Anchor on a unique neighbouring call, then walk to the target](howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-2.md) — advanced
- [Anchor on a UTF-8 label literal in the method](howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-3.md) — advanced
- [Match the instruction that consumes the result](howto/patching/transpilers/identify-a-call-by-its-argument-il/identify-a-call-by-its-argument-il-4.md) — advanced

### [Inserting calls into a game method at an IL anchor](howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/overview.md)

How KSA mods add draw, filter or snapshot calls inside a stock method by inserting instructions before or after an anchor, and how labels and exception-block markers decide whether jumps reach the insertion

- [Move labels and begin-exception markers onto the inserted instruction](howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-1.md) — advanced
- [Leave labels on the anchor so a jump skips the addition](howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-2.md) — intermediate
- [Snapshot state before a stock call, restore it in a paired prefix](howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-3.md) — intermediate
- [Insert before the first and after the last instance of a pass boundary](howto/patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/insert-calls-into-a-game-method-at-an-il-anchor-4.md) — advanced

### [Swapping one call inside a game method with a transpiler](howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)

How KSA mods make a large game method call their own code instead of one helper without copying the method, in five shapes, from a same-signature operand edit to adapters that translate arguments and results

- [Same-signature static replacement, operand-only edit](howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-1.md) — intermediate
- [Wider signature with an extra ldarg.0 appended](howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-2.md) — advanced
- [Replacement that does mod work, then calls the original](howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-3.md) — advanced
- [Gate helper that skips the call for the mod's own condition](howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-4.md) — advanced
- [Adapter that fixes up arguments and converts the result back](howto/patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/swap-one-call-inside-a-game-method-with-a-transpiler-5.md) — advanced

### [Writing a Harmony transpiler for a KSA method](howto/patching/transpilers/write-a-harmony-transpiler/overview.md)

How to read the real IL of a KSA method, build the instruction rewrite, prove the match count and survive Harmony re-running transpilers, with four worked shapes: retarget a call, insert at an anchor, replace an instruction run and filter a read

- [Retarget the operand of one call (same signature)](howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-1.md) — intermediate
- [Insert a call before a validated anchor](howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-2.md) — advanced
- [Replace an instruction run with a helper that takes a local by reference](howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-3.md) — advanced
- [Filter one value read through a registry check](howto/patching/transpilers/write-a-harmony-transpiler/write-a-harmony-transpiler-4.md) — advanced

## Internals

Reaching what the game does not expose: private members through reflection and delegates, private or overloaded patch targets, and methods the JIT inlines.

### [Reading, writing and calling private game members](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/overview.md)

How KSA mods reach private and internal members that the game exposes no API for, with AccessTools field refs, plain reflection, method delegates and UnsafeAccessor, and how each form fails when a member moves

- [AccessTools static field refs bound once, replay stock's writes](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-1.md) — intermediate
- [Plain GetField on internal registries and a generic holder for instance fields](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-2.md) — intermediate
- [Typed FieldRefAccess, boxed private enum members and by-ref lookups](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-3.md) — advanced
- [AccessTools.MethodDelegate with a delegate type that mirrors in and ref modifiers](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-4.md) — advanced
- [Delegates over private editor helpers reused in a replacement drag](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-5.md) — advanced
- [UnsafeAccessor StaticField on the k__BackingField with UnsafeAccessorType](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-6.md) — advanced
- [Copied injector in a consumer mod with the same accessor](howto/patching/internals/call-private-game-members-through-reflection-and-delegates/call-private-game-members-through-reflection-and-delegates-7.md) — advanced

### [Patching private and overloaded game methods by name](howto/patching/internals/patch-a-private-game-method-by-name/overview.md)

How a Harmony patch attaches to a private, protected or overloaded KSA method that cannot be written with typeof or nameof, with string names, manual lookups, explicit parameter arrays, TargetMethods and injected fields, and how each fails when the target moves

- [HarmonyPatch attribute with a string method name, applied by PatchAll](howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-1.md) — beginner
- [Manual AccessTools lookup with a degrade path when the method is gone](howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-2.md) — intermediate
- [Explicit parameter arrays with MakeByRefType for overloaded and by-reference targets](howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-3.md) — intermediate
- [Attribute argument types that bind by assignability](howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-4.md) — intermediate
- [TargetMethods with per-name AccessTools lookups and the __args array](howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-5.md) — advanced
- [Reading a private instance field with a triple-underscore parameter](howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-6.md) — intermediate
- [Prefer __instance and a public member over a field injector that matches nothing](howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-7.md) — beginner

### [Changing behaviour of a game method marked AggressiveInlining](howto/patching/internals/patch-around-aggressive-inlining/overview.md)

Why a Harmony prefix or postfix on a KSA method marked AggressiveInlining can silently never run, what a local test shows about when the JIT bypasses a detour, and four patterns the pinned mods use to change behaviour without depending on it

- [Rewrite the one caller's call site instead of the inlined callee](howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-1.md) — advanced
- [Patch the non-inlined entry points that reach the inlined method too](howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-2.md) — intermediate
- [Postfix the single non-inlined outer method instead of a per-vehicle callee](howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-3.md) — intermediate
- [Bracket the non-inlined methods that enclose an inlined struct callback](howto/patching/internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-4.md) — advanced
