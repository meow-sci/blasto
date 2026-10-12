# Game Feature

* [Identifying the right call in a method that makes several similar calls](overview.md) - How KSA transpilers pick one instruction among many similar ones by what surrounds it: the loads that feed its arguments, a unique neighbouring call, a UI label literal, or the instruction that consumes its result

# How-To

* [Anchor on a unique neighbouring call, then walk to the target](identify-a-call-by-its-argument-il-2.md) - When the target call is not distinctive, anchor on a call that occurs exactly once nearby and walk to it: from RenderDrawData back to the nearest BeginRenderPass, from the end to the last EndRenderPass, and from Burn.Create back at most 160 instructions to the PrimaryButton call
* [Anchor on a UTF-8 label literal in the method](identify-a-call-by-its-argument-il-3.md) - Find a stock submenu in BurnContextMenu.Draw by the UTF-8 label literal passed to ImGui.BeginMenu, by reading the bytes of the u8 literal's static data field, then find the matching EndMenu and insert before it
* [Match the instruction that consumes the result](identify-a-call-by-its-argument-il-4.md) - Rewrite Vehicle.get_Target only where it feeds a null test (brtrue, brfalse, or ldnull then ceq or cgt.un), in four methods chosen by TargetMethods, so the game's own null checks see a mod-supplied root target while uses of Target as a value are left alone
* [Scan backwards through the argument loads to the previous call](identify-a-call-by-its-argument-il-1.md) - Tell the atmospheric ComputeDrag call from the ocean one by walking back from each call through its argument loads for the discriminating field, stopping at the previous call, with the real build 5554 IL of both calls
