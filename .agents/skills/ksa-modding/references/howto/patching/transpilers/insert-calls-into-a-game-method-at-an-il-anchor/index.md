# Game Feature

* [Inserting calls into a game method at an IL anchor](overview.md) - How KSA mods add draw, filter or snapshot calls inside a stock method by inserting instructions before or after an anchor, and how labels and exception-block markers decide whether jumps reach the insertion

# How-To

* [Insert before the first and after the last instance of a pass boundary](insert-calls-into-a-game-method-at-an-il-anchor-4.md) - Append four instructions (ldloc.1, ldloc.0, ldc.i4.0, call) after the last EndRenderPass in Program.RenderGame to run the post-ImGui shader chain outside the render pass, and why hard-coded local indices are the weak point
* [Leave labels on the anchor so a jump skips the addition](insert-calls-into-a-game-method-at-an-il-anchor-2.md) - Insert one call before PopWidgetStyle on the Mods settings page deliberately without taking the anchor's labels, so that when stock jumps over the page the added sections are skipped too; contrast with moving labels and say how to decide
* [Move labels and begin-exception markers onto the inserted instruction](insert-calls-into-a-game-method-at-an-il-anchor-1.md) - Insert a call before an anchor so jumps to the anchor reach it and it stays inside the same try region, by moving labels and begin-exception markers, plus a click gate that filters a stack bool
* [Snapshot state before a stock call, restore it in a paired prefix](insert-calls-into-a-game-method-at-an-il-anchor-3.md) - Capture a local layout value from inside Program.DrawMenuBar by injecting a stack-neutral call before the first SetCursorPosY, then use it from a prefix on the public DrawProgramMenusHook, with an honest look at the timing of the snapshot
