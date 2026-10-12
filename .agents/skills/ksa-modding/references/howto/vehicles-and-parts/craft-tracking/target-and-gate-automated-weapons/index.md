# Game Feature

* [Picking and gating targets for automated weapons](overview.md) - Closest-approach threat classification, sensor cone and lock dwell with terrain masking, and an explainable ladder of fire gates with a shared per-craft target tally

# How-To

* [Classify threats by closed-form closest approach with median-filtered acceleration](target-and-gate-automated-weapons-1.md) - Rank contacts by time to closest approach from paired relative samples, with notch, cone and clutter rejects ahead of the CPA and a median window guarding the accelerometer
* [Gate automated fire with an ordered ladder and a per-craft target tally](target-and-gate-automated-weapons-3.md) - Express fire permission as ordered named gates whose first refusal explains why, and share rounds-per-target across launchers through a tally rebuilt each step
* [Model a radar profile with a search cone, lock dwell and terrain-masked line of sight](target-and-gate-automated-weapons-2.md) - Reject contacts cheapest first, sample the height field only in a closed-form ambiguous band, and promote a track to a lock after a dwell carried by object identity
