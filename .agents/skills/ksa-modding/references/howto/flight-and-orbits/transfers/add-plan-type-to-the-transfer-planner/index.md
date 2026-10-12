# Game Feature

* [Add plan types to the stock Transfer Planner](overview.md) - Extend the stock Transfer Planner window with a custom plan type by registering it, injecting UI and call-site changes into the plan window, opening and preselecting the planner from a mod, and letting it target bodies on hyperbolic orbits

# How-To

* [Hand a retargeted transfer to the stock Create path](add-plan-type-to-the-transfer-planner-4.md) - Return a mod-computed Burn from a same-signature replacement of Burn.Create so the planner's own CREATE button queues it, with a click gate for repeated clicks and a fallback to the stock burn on any failure
* [Inject UI and swap a call in DrawPlanWindow with a transpiler that validates its IL anchors](add-plan-type-to-the-transfer-planner-2.md) - Rewrite the stock plan window in anchored passes (insert a call before the transfer controls, a fallback before the style pop, swap Burn.Create and gate the CREATE click), moving labels and exception blocks correctly and failing soft when anchors are missing
* [Let the planner target hyperbolic bodies with an XML patch, NaN-period patches and a Harmony finalizer](add-plan-type-to-the-transfer-planner-5.md) - Add Mass and SphereOfInfluence to comets in the system XML, then patch each stock assumption (target list, Hohmann time, window sizing, alignment, intercept search, closest approaches) that breaks for an eccentricity of one or more
* [Open and preselect the stock Transfer Planner with AccessTools FieldRefs and delegates bound once](add-plan-type-to-the-transfer-planner-3.md) - Bind the planner's private static fields and SetTransferInfo once, replay stock's combo-change side effects in order, and degrade to just opening the window when a key goes missing
* [Register plan types and redraw the stock window in a skip-original prefix under the same id, title and signature](add-plan-type-to-the-transfer-planner-1.md) - Insert TransferType entries into TransferPlanner.TransferTypes, take over DrawPlanWindow for your own keys with a prefix that returns false, keep stock's window identity, and replay stock's reset when the type changes
