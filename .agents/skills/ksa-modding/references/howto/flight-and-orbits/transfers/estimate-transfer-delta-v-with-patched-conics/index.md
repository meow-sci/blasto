# Game Feature

* [Estimate transfer delta-v in closed form with patched conics](overview.md) - Compute Hohmann-style departure and arrival delta-v between circular reference orbits around one body and across spheres of influence with the patched-conic approximation and the game's own orbit and Mu data

# How-To

* [Estimate a coplanar Hohmann transfer to a circular altitude from the current orbit](estimate-transfer-delta-v-with-patched-conics-1.md) - Compute departure, arrival and total delta-v and the transfer time for a two-burn Hohmann transfer from the vehicle's radius to a target altitude using vis-viva, as a one-line readout
* [Price transfers with hub Hohmann legs wrapped in Oberth burns, cached per body pair](estimate-transfer-delta-v-with-patched-conics-2.md) - Compute the departure and arrival burns between bodies that share a hub as Hohmann legs around the hub, convert each leg to an Oberth ejection or capture from the end body's low orbit, and add comet, multiple-star, ascent, landing and plane-change terms
