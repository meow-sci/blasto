# Kittens

Characters and EVA kittens - reaching the avatar, skeleton and animation catalog, the character XML, spawning kittens, per-kitten materials, animation clips and processors, posing, held props, seated crew, hiding a kitten from one view, locomotion facts and turning a kitten into a flyable vessel.

## Characters

Reaching and creating kittens: the avatar and skeleton, the character asset XML, spawning EVA kittens and restoring per-kitten state after a load.

### [Read and extend the character asset XML (characters, fur, expressions, attachments)](howto/kittens/characters/define-a-kitten-character-in-asset-xml/overview.md)

How CharacterAssets.xml composes a Character from shared core, textures, fur, expression, animation and attachment blocks, how the game registers, lists and renders it, and what no reference mod has tested

- [Compose a Character from the shared asset blocks](howto/kittens/characters/define-a-kitten-character-in-asset-xml/define-a-kitten-character-in-asset-xml.md) — advanced

### [Reach a kitten's avatar, skeleton, bones and animation catalog](howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/overview.md)

How a mod gets from a KittenEva to its CharacterAvatar, Skeleton, named bones, private clip fields and materials, and where in the frame the kitten is animated and drawn

- [Reach the avatar with one reflection hop and resolve bones by name](howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-1.md) — intermediate
- [Build a live clip catalog from cached FieldInfo reads with a visible unresolved list](howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-2.md) — intermediate
- [Walk the avatar's struct fields down to the live MaterialIndices arrays](howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-3.md) — advanced
- [Know where and when a kitten is animated and drawn, per viewport](howto/kittens/characters/reach-a-kittens-avatar-and-skeleton/reach-a-kittens-avatar-and-skeleton-4.md) — advanced

### [Restore kitten colours, clips and tuning after a save load](howto/kittens/characters/restore-kitten-customizations-after-a-load/overview.md)

Why every mod-held reference to a kitten is dead after a native load and three ways unscience captures per-kitten state against vehicle ids and rebinds it to the restored KittenEva objects

- [Rebind cloned kitten material sets to the restored kittens by vehicle id](howto/kittens/characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-1.md) — advanced
- [Restore kitten colours by material name, only where the asset object is still the one written](howto/kittens/characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-2.md) — advanced
- [Save the forced clip with its label, source and phase, plus the walking tuning, and rebind them to the restored kitten](howto/kittens/characters/restore-kitten-customizations-after-a-load/restore-kitten-customizations-after-a-load-3.md) — advanced

### [Spawn an EVA kitten from code](howto/kittens/characters/spawn-an-eva-kitten-from-code/overview.md)

How a mod creates a real KittenEva with a backpack part, a chosen character and an orbit beside another vessel, registers it with its parent body and avoids racing the vehicle solver

- [Replicate EVADoor.CreateKittenEva with the public KittenEva constructor, place the kitten beside a vessel, then track and despawn it](howto/kittens/characters/spawn-an-eva-kitten-from-code/spawn-an-eva-kitten-from-code-1.md) — advanced
- [Build a KittenEva from a saved craft that names a Character, inside the shapes lock](howto/kittens/characters/spawn-an-eva-kitten-from-code/spawn-an-eva-kitten-from-code-2.md) — advanced

## Animation

Driving the kitten animation system: anim processors, forced clips, facial expressions and seated crew.

### [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](howto/kittens/animation/animate-and-draw-seated-kitten-crew/overview.md)

How a seat builds and draws its own KittenRenderable, only for the controlled vehicle, and three mod techniques for posing a seated kitten, drawing crew of craft nobody is flying and sitting in a chair to fly a different craft

- [Attach a pose processor to a seat's kitten each time the seat rebuilds it, and stop the idle actions fighting the hands](howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-1.md) — advanced
- [Draw seated crew on craft nobody is flying with a Harmony postfix on Vehicle.UpdateSeatedCrewRenderData](howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-2.md) — intermediate
- [Sit in a chair to fly another craft - swap ControlledVehicle, borrow a camera window and restore everything on log-off](howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-3.md) — advanced

### [Make a kitten pull a facial expression](howto/kittens/animation/make-a-kitten-pull-a-facial-expression/overview.md)

How the five authored faces are mixed over a kitten's pose by CatExpressionAnim processors, why writing the game's reactive processor never holds, and two ways to drive faces and the eye, ear and personality weights

- [Own a CatExpressionAnim appended after the game's four, and bust its pose cache on every swap](howto/kittens/animation/make-a-kitten-pull-a-facial-expression/make-a-kitten-pull-a-facial-expression-1.md) — intermediate
- [Re-apply eye, ear and personality weights each frame, cap the reactive face with Math.Min, and restore the originals on unbind](howto/kittens/animation/make-a-kitten-pull-a-facial-expression/make-a-kitten-pull-a-facial-expression-2.md) — intermediate

### [Force a kitten to play a chosen clip, freeze it or change its speed, and hold values the game rewrites](howto/kittens/animation/play-a-specific-kitten-animation-clip/overview.md)

Why a clip set from a StarMap callback is overwritten every frame, why a Harmony prefix on AnimatedRenderable.UpdateAnimation is the point where a forced clip, a freeze or a rate multiplier still lands, and the patch-the-consumer pattern for any per-frame rewrite

- [Force a clip from a prefix on AnimatedRenderable.UpdateAnimation, with freeze, restart, blend time and rate](howto/kittens/animation/play-a-specific-kitten-animation-clip/play-a-specific-kitten-animation-clip.md) — advanced

### [Pose a kitten's bones and aim its limbs with an IAnimProcessor](howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/overview.md)

How a mod edits a kitten's sampled local pose or its world transforms before skinning with an IAnimProcessor, aims an arm at a point with one-bone or two-bone solves, converts between model space and the world, and draws a stepped or turned kitten without moving its body

- [Edit the local pose in UpdateLocalPose, on a simulated-time clock, with the distance cull in mind](howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-1.md) — intermediate
- [Overwrite world transforms in UpdateSkeleton once per view](howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-2.md) — advanced
- [Aim an arm at a point with a one-bone shoulder turn](howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-3.md) — intermediate
- [Solve a two-bone arm in seat-relative model space with a clamped reach and an outward-and-down elbow](howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-4.md) — advanced
- [Convert a standing kitten's model space to the ecliptic or a camera's ego frame](howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-5.md) — advanced
- [Draw a kitten stepped, turned and leaning with a stance matrix on the root bones, without moving its body](howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/pose-kitten-bones-with-an-anim-processor-6.md) — advanced

## EVA

EVA kittens as simulated bodies: locomotion facts, steering, flying a kitten as a rocket and editing it in the vehicle editor.

### [Kitten locomotion, modes and the servo, from the decompiled game](howto/kittens/eva/kitten-locomotion-and-servo-reference/overview.md)

A reference for how a walking, floating or swimming kitten is simulated - the IsKitten flag on the worker snapshot, LocomotionMode and LocomotionState, KittenLocomotion.Advance and its steppers, the KittenServoPrecomp traction and upright gains, wake rules and the tuning struct copied to the worker

- [Find the kitten branches in the worker and patch them from outside the solver](howto/kittens/eva/kitten-locomotion-and-servo-reference/kitten-locomotion-and-servo-reference-1.md) — advanced
- [Tune kitten locomotion and animation parameters live from an ImGui panel](howto/kittens/eva/kitten-locomotion-and-servo-reference/kitten-locomotion-and-servo-reference-2.md) — intermediate

### [Open the stock vehicle editor on a live EVA kitten, show it upright and keep its attachment nodes](howto/kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/overview.md)

How a mod opens the stock vehicle editor on a live EVA kitten by setting Program.EditorFlag at the physics handoff, shows the kitten upright, and gives it attachment nodes that persist through saves

- [Open the editor on a live kitten with Program.EditorFlag at the handoff and root guards on every editor entry point](howto/kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/open-the-vehicle-editor-on-a-live-eva-kitten-1.md) — advanced
- [Draw the avatar from a ClearBuckets postfix and re-base the editor camera upright](howto/kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/open-the-vehicle-editor-on-a-live-eva-kitten-2.md) — advanced
- [Give one kitten private attachment nodes that survive saves, blueprints and copies](howto/kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/open-the-vehicle-editor-on-a-live-eva-kitten-3.md) — advanced

### [Make a walking kitten move and face along the look direction, and drive it with the mouse](howto/kittens/eva/steer-a-walking-kitten/overview.md)

How the stock grounded walk takes its heading from the main camera, how two first-person mods patch KittenLocomotion.StepGrounded to supply their own heading across threads, and how a captured mouse drives a kitten action such as a putter swing

- [Walk and face along the look with a StepGrounded prefix and postfix and a volatile heading](howto/kittens/eva/steer-a-walking-kitten/steer-a-walking-kitten-1.md) — advanced
- [Capture the cursor and drive a kitten action with raw mouse deltas](howto/kittens/eva/steer-a-walking-kitten/steer-a-walking-kitten-2.md) — advanced

### [Fly an EVA kitten as an ordinary rocket vessel, with vessel input and the flight HUD](howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/overview.md)

How a mod makes one chosen kitten take the vessel branches of physics, input and the HUD - flipping the IsKitten snapshot flag, publishing membership to workers, rotating the control frame and swapping the backpack RCS map - and two ways to call the base Vehicle handlers that the kitten overrides

- [Fly an EVA kitten as a vessel - IsKitten postfix, Ctrl2Body postfix, queued mode switches, input and HUD transpilers](howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/treat-an-eva-kitten-as-a-rocket-1.md) — advanced
- [Call the base Vehicle handlers through a Harmony reverse patch onto a static stub](howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/treat-an-eva-kitten-as-a-rocket-2.md) — advanced
- [Call the base through a DynamicMethod non-virtual call so other mods' patches still run](howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/treat-an-eva-kitten-as-a-rocket-3.md) — advanced

## Appearance

How a kitten looks: materials and tints, helmet and visor, held props and per-viewport body hiding.

### [Attach a prop to a kitten's hand or another bone at runtime, building the prop mesh from mod assets](howto/kittens/appearance/attach-a-prop-to-a-kitten-bone/overview.md)

How a mod hangs its own mesh on a kitten bone with a CharacterAvatar CosmeticAttachment, built from mod glTF and material assets, and how to place it exactly on the drawn hand per view

- [Add a CosmeticAttachment with a static offset and build the prop mesh from the mod's glTF and material](howto/kittens/appearance/attach-a-prop-to-a-kitten-bone/attach-a-prop-to-a-kitten-bone-1.md) — intermediate
- [Solve the attachment transform in UpdateSkeleton against the view's wrist](howto/kittens/appearance/attach-a-prop-to-a-kitten-bone/attach-a-prop-to-a-kitten-bone-2.md) — advanced

### [Hide a kitten's body from the main view only, keeping its shadow](howto/kittens/appearance/hide-a-kitten-body-from-one-viewport/overview.md)

How a mod hides a kitten body from the main view only by folding its skeleton per viewport and restoring the full pose in the shadow pool, and what the engine HideHead switch does instead

- [Fold all bones onto the head with zero-scale matrices, then give the shadow pass the full-body pose](howto/kittens/appearance/hide-a-kitten-body-from-one-viewport/hide-a-kitten-body-from-one-viewport-1.md) — advanced
- [Fold onto the wrist and keep the club-holding paw, or use KittenRenderable.HideHead per view](howto/kittens/appearance/hide-a-kitten-body-from-one-viewport/hide-a-kitten-body-from-one-viewport-2.md) — advanced

### [Tint kitten fur, body and eyes, and hide the visor, helmet or MMU](howto/kittens/appearance/tint-and-restyle-kitten-appearance/overview.md)

How mods recolour kittens by changing AlbedoColor in the shared GPU material buffer or in per-kitten material clones, and hide the visor, helmet or MMU with a transpiler or the attachment flags

- [Recolour every kitten with one staged float4 write into the shared material buffer](howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-1.md) — intermediate
- [Clone the materials per kitten, swap the MaterialIndices handles and stay inside the 512-slot pool](howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-2.md) — advanced
- [Bind mod-owned material clones to the EVA renderables and restore them conditionally](howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-3.md) — advanced
- [Transpile the single VisorMesh.Draw call into a gated forwarder](howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-4.md) — advanced
- [Hide the helmet, visor and MMU per avatar by clearing Helmet.Enabled and Mmu.Enabled](howto/kittens/appearance/tint-and-restyle-kitten-appearance/tint-and-restyle-kitten-appearance-5.md) — beginner
