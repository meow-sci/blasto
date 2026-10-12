# Game Feature

* [Hide part meshes or subparts without removing them](overview.md) - Remove a window pane, a model or a missile from the picture while the part stays in the vessel, by filtering instance lists, scaling a subpart to nothing or moving it, and also show IVA interiors from outside and draw kitten equipment in the right order

# How-To

* [Flip Template.Internal from a lazily installed dedicated Harmony instance](hide-parts-or-subparts-from-rendering-5.md) - Make cabin interiors render outside IVA with a cheat that installs its constructor postfix only while on, tracks the templates it changed, and unpatches itself when switched off
* [Hide part models by compacting the instance lists around PartTreeRenderData.Compose](hide-parts-or-subparts-from-rendering-1.md) - Drop selected parts or whole models from the frame's draw lists with a shared prefix/postfix pair on the three PartTreeRenderData compose methods, leaving the parts alive in the vessel
* [Pose or hide subparts from StarMapAfterGui with an AfterOnFrame fallback](hide-parts-or-subparts-from-rendering-3.md) - Write a subpart's parent-relative transform every frame from a StarMap hook, resolve subparts by instance Id suffix, hide by 0.001 scale, and latch the work so it also runs when the HUD is hidden
* [Render kitten equipment parts before the instance upload with a nonvirtual Vehicle.UpdateRenderData call](hide-parts-or-subparts-from-rendering-6.md) - Submit an EVA kitten's part tree early from a prefix on PartModelRenderer.UpdateRenderData, calling the patched Vehicle.UpdateRenderData nonvirtually and skipping the late duplicate with a Priority.First prefix
* [Scale a pre-declared subpart to nothing until it is needed](hide-parts-or-subparts-from-rendering-2.md) - Declare spare subparts in the part XML, park them at 0.001 scale, and rewrite their parent-relative transform from mod code each frame to hide, seat or fly them
* [Show IVA interiors from outside with an owner-counted Template.Internal requirement](hide-parts-or-subparts-from-rendering-4.md) - Flip the shared PartModelModule.Template.Internal flag on every loaded and newly constructed model so cabins render outside IVA, with several owners sharing one requirement count and an editor-only reveal bracketed by a finalizer
