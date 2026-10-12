# Game Feature

* [Disable collisions for one vessel without touching others](overview.md) - How a mod takes a single vessel out of Bepu collision detection for the duration of each constraint-sim pass, by swapping its shape for an empty one and restoring it in a finalizer

# How-To

* [BodyReference.SetShape(default) in a prefix with a restoring finalizer](suppress-collisions-for-one-vessel.md) - Remove each suppressed source vessel's collidable from the Bepu broad phase for one constraint-sim pass with SetShape(default), and put the captured TypedIndex back in a Harmony finalizer even when the pass throws
