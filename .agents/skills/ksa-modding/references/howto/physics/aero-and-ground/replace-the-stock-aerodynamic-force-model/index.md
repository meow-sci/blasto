# Game Feature

* [Replace the stock aerodynamic force model and measure the active one](overview.md) - How a mod swaps KSA's box drag for its own lift and drag on every vehicle inside the solver, computes Mach from the game atmosphere, and measures whatever model is active by calling the force function on a copied state

# How-To

* [Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator](replace-the-stock-aerodynamic-force-model-1.md) - Replace KSA's atmospheric drag on every vehicle with your own lift and drag by swapping the operand of one ComputeDrag call for a same-signature method, with Mach computed from the game atmosphere
* [Speed of sound from the atmosphere reference for a measurement tool](replace-the-stock-aerodynamic-force-model-2.md) - Measure the drag and lift of whatever aerodynamic model is active by calling ComputeDerivatives on a copied vehicle state at chosen Mach numbers and angles of attack, with Mach derived from the game atmosphere on the main thread
