# Game Feature

* [Kitten locomotion, modes and the servo, from the decompiled game](overview.md) - A reference for how a walking, floating or swimming kitten is simulated - the IsKitten flag on the worker snapshot, LocomotionMode and LocomotionState, KittenLocomotion.Advance and its steppers, the KittenServoPrecomp traction and upright gains, wake rules and the tuning struct copied to the worker

# How-To

* [Find the kitten branches in the worker and patch them from outside the solver](kitten-locomotion-and-servo-reference-1.md) - A map of the seams mods use around the kitten character controller - read KittenLocomotionTuning.Current, patch KittenLocomotion.StepGrounded to supply a heading, flip VehicleUpdateState.IsKitten to take the vessel branches - with what thread each runs on and what is safe there
* [Tune kitten locomotion and animation parameters live from an ImGui panel](kitten-locomotion-and-servo-reference-2.md) - Kitten Animations passes ref KittenLocomotionTuning.Current.Field to ImGui.DragFloat for twenty animation fields in five groups, with tooltips, drag ranges that are not clamps, a reset that copies only those fields from Default, and a live readout of the blend weights
