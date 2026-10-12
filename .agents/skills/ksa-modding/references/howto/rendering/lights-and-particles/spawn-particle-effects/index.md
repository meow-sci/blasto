# Game Feature

* [Spawn particle effects and explosions from a mod](overview.md) - Take emitters from the engine's fixed pool, either from XML-declared ParticleEmitter assets or built in C#, anchor them, return them correctly, and fire the game's own explosion presets where no vehicle hosts the burst

# How-To

* [Build a ParticleEmitterReference in C# with SpawnMode.Burst](spawn-particle-effects-2.md) - Burst celebration or danger particles at a vessel or a kitten's face by taking one emitter from the pool, initializing it from a ParticleEmitterReference built in code, and attaching it with the vehicle's bubble origin
* [Fire the game's own ExplosionSystem presets from a point anchored on the planet](spawn-particle-effects-3.md) - Set off a stock or custom Explosion preset anywhere by building an ExplosionContext whose BubbleOrigin is fixed in a celestial's body frame, with real ambient pressure and a preset chosen by fireball size
* [Take XML-declared ParticleEmitter assets by id from the engine pool and give them back correctly](spawn-particle-effects-1.md) - Declare ParticleEmitter assets in your own XML, acquire them with GetAndInitializeEmitters, re-anchor held emitters on a celestial every frame, and always Kill them before returning them to the shared pool
