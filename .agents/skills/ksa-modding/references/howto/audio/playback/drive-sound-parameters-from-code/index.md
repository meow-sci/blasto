# Game Feature

* [Drive a sound's Param curves from code and reuse stock sounds](overview.md) - How the named parameters a sound's Param curves listen to are set with SetParameter and a KeyHash, when the engine applies them, and how to reuse Core's engine and explosion sounds for mod-simulated sources

# How-To

* [Borrow Core's engine loop for a mod-simulated projectile with Throttle pinned open](drive-sound-parameters-from-code-3.md) - Play DefaultEngineSoundBehavior from ModLibrary for each burning round and set Throttle to 1 on start, because Core's loop maps Throttle 0 to a tenth of its volume and half its pitch
* [Play Core's engine sound for a mod-simulated rocket by mirroring Vehicle.UpdateAudio](drive-sound-parameters-from-code-1.md) - Reuse DefaultEngineSoundBehavior for pushes the mod applies itself by starting it paused through GameAudio.PlaySound, feeding Throttle, Distance, Pressure and Iva every frame, and unpausing once
* [Play Core's explosion sound on your own schedule with hand-set parameters](drive-sound-parameters-from-code-2.md) - Strip the SoundEvent from a copied Core explosion preset so the engine stays silent, then play the same MultiSound yourself with Distance, Pressure and Intensity set so its layers mix correctly
