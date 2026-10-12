# Game Feature

* [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](overview.md) - Run a per-vehicle guidance loop on the main thread right before each vehicle's worker snapshot, claim vehicles per mod, detect when the player takes over attitude or burn mode, and release only what you changed

# How-To

* [Adopt a separated booster as its own guided vehicle](run-an-autopilot-for-every-vehicle-each-step-2.md) - Record the detached root parts before a staging request fires, then match the new vehicle that appears after the split against those ids and give it guidance state, retrying the engage until the part tree has settled
* [Claim vehicles per mod, hold burn mode and compare exact fields to detect a takeover](run-an-autopilot-for-every-vehicle-each-step-4.md) - Keep one claimant per Vehicle in a ConditionalWeakTable, hold the stock burn mode in Manual and give Auto back only for the same loaded burn, and snapshot and compare the exact flight-computer fields to notice the player taking over
* [Fly a vehicle that is not the controlled one, then hand it back](run-an-autopilot-for-every-vehicle-each-step-3.md) - Command another craft's throttle through Vehicle.ProcessInput and its staging through the sequence list, point it with a Custom target, and release it so the player gets back what they had
* [Step a per-vehicle autopilot from a PrepareWorker prefix with takeover checks and a menu off switch](run-an-autopilot-for-every-vehicle-each-step-1.md) - Run guidance for every vehicle right before its worker snapshot, check per actuator whether the player took over, count failures instead of throwing every frame, and release craft when the mod is switched off
