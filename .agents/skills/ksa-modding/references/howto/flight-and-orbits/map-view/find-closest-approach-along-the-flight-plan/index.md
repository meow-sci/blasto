# Game Feature

* [Find the closest approach to a target along a trajectory](overview.md) - Locate the time and distance of closest approach to a target along the flight plan or a straight-line prediction, classify what is coming close, and write the result into the stock gauge digits

# How-To

* [Compute the closest point of approach from straight-line relative motion and rank contacts by time to it](find-closest-approach-along-the-flight-plan-2.md) - Difference two same-instant ecliptic samples, solve the time of closest approach of straight-line relative motion clamped to a horizon, classify a contact as a threat by its miss distance rather than its closing speed, and rank threats by how soon they arrive
* [Search the flight plan for the closest approach with uniform and log-spaced samples and golden-section refinement, then write it into the stock TGT gauge](find-closest-approach-along-the-flight-plan-1.md) - Evaluate the distance to a target along every coast patch of Vehicle.FlightPlan, sample uniformly and log-spaced, refine local minima with golden-section search, cache the result per vehicle, and override the stock TGT closest-approach rollers from a Program.UpdateGauges postfix
