# Game Feature

* [Plan and fly a powered landing](overview.md) - Plan a deorbit burn to a site on a rotating body, fly a fuel-optimal powered descent, and finish with a hover and touchdown under engine power

# How-To

* [Fly a 6-DOF landing MPC with the solve on a dedicated background thread](plan-and-fly-a-powered-landing-2.md) - Run a successive-convexification landing model-predictive controller whose solves take milliseconds to seconds on a per-vehicle worker thread, with one request and one result slot, an estimated acceleration bias, a wall-clock budget and a node ladder
* [Fly a fuel-optimal descent with G-FOLD solved synchronously under a time limit](plan-and-fly-a-powered-landing-1.md) - Build a site-fixed landing frame on a rotating body, solve a lossless-convexification powered descent inside the per-vehicle step with a solver time limit, and fly the committed trajectory with feed-forward thrust and PD feedback
* [Hover and touch down with a sink-profile velocity controller and terrain height below the craft](plan-and-fly-a-powered-landing-3.md) - Land the last stretch with per-axis velocity PID controllers that follow a quadratic sink-rate profile, measure height to the terrain directly below the craft, refuse craft that cannot hover, and detect touchdown from the game's contact flag
* [Plan a deorbit burn to a landing site with a time-sliced Lambert search, then let a stock node fly it](plan-and-fly-a-powered-landing-4.md) - Search for a deorbit transfer to a rotating site as a C# iterator advanced a few milliseconds per frame under work budgets, then load it as a stock maneuver node and let the stock autopilot compute the ignition timing
