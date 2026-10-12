# Game Feature

* [Swapping one call inside a game method with a transpiler](overview.md) - How KSA mods make a large game method call their own code instead of one helper without copying the method, in five shapes, from a same-signature operand edit to adapters that translate arguments and results

# How-To

* [Adapter that fixes up arguments and converts the result back](swap-one-call-inside-a-game-method-with-a-transpiler-5.md) - Turn the one GetInitialKinematicStateForLocation call in Vehicle.TeleportToLocation into an adapter that receives the eight original arguments plus the vehicle, substitutes head-up proxy bounds for an active Iron Man kitten, calls the native helper and converts attitude and rates back
* [Gate helper that skips the call for the mod's own condition](swap-one-call-inside-a-game-method-with-a-transpiler-4.md) - Turn a callvirt Draw into a static gate that forwards to Draw only while a flag is clear, and redirect IsLargeEnoughToRender and MeanRadius reads in two Vehicle methods to scale-aware mirrors, with a guarded count check on each and a reason a getter patch would be wrong
* [Replacement that does mod work, then calls the original](swap-one-call-inside-a-game-method-with-a-transpiler-3.md) - Swap the generic BeginRenderPass call in Program.RenderGame for a method with the same stack effect that records the mod's passes and then issues the original BeginRenderPass itself, because no method boundary exists at that point for a prefix
* [Same-signature static replacement, operand-only edit](swap-one-call-inside-a-game-method-with-a-transpiler-1.md) - Replace one call in a big game method with a static method that has exactly the same parameter list and return type so only the instruction's operand changes, with the replacement free to call the original through a delegate or directly
* [Wider signature with an extra ldarg.0 appended](swap-one-call-inside-a-game-method-with-a-transpiler-2.md) - Replace NavigationTarget.Create inside two private Vehicle methods with a method that takes the original four arguments plus the vehicle, by inserting ldarg.0 before the call and moving the call site's labels and exception markers onto it
