# Game Feature

* [Steer by predicted miss (zero-effort-miss guidance) and model homing sensors](overview.md) - Steer a missile or falling object by its line-of-sight rate, its zero-effort miss or its predicted impact, and model a sensor's field of view, line of sight and target loss for homing objects

# How-To

* [Model a homing sensor's gimbal limit, raycast occlusion, decoys and uplink loss](steer-by-zero-effort-miss-4.md) - Gate a homing round on a seeker field of view recomputed every sub-step, cast a sensor beam against craft meshes with Part.RayCastEgo and against terrain, judge each decoy once, remember an emitter that goes silent, and drop a command uplink when the launcher loses sight
* [Steer a falling store by its predicted impact and take a ground designation as its input](steer-by-zero-effort-miss-3.md) - Predict where a falling store lands under gravity and current drag, push square to its airflow to move that landing onto a designated place on the ground, and decide whether a new click corrects the store in the air or aims the next one
* [Steer a mod-simulated round by true proportional navigation on a back-dated target sample](steer-by-zero-effort-miss-1.md) - Null the line-of-sight rotation between a round and a KSA vehicle with true proportional navigation, with the target sample moved back to the round's own epoch, gravity compensation, a lateral-only command and an airframe g limit
* [Steer on a predicted impact point with a zero-effort-miss law](steer-by-zero-effort-miss-2.md) - Close the loop on the miss between a predicted impact point and a site with a gain over time-to-go squared, a measured impact Jacobian, a time-to-go floor, the integral term off for long glides and a wall-clock re-solve cadence
