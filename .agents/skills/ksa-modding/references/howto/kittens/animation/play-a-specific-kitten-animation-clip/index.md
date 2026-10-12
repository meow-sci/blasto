# Game Feature

* [Force a kitten to play a chosen clip, freeze it or change its speed, and hold values the game rewrites](overview.md) - Why a clip set from a StarMap callback is overwritten every frame, why a Harmony prefix on AnimatedRenderable.UpdateAnimation is the point where a forced clip, a freeze or a rate multiplier still lands, and the patch-the-consumer pattern for any per-frame rewrite

# How-To

* [Force a clip from a prefix on AnimatedRenderable.UpdateAnimation, with freeze, restart, blend time and rate](play-a-specific-kitten-animation-clip.md) - Kitten Animations keeps a driver that a Harmony prefix on AnimatedRenderable.UpdateAnimation feeds, compares the model to the bound kitten by reference, re-applies the forced clip after the game's choice, and also holds processor weights the game rewrites
