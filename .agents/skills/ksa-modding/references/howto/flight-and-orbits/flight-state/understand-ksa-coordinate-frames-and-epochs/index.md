# Game Feature

* [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](overview.md) - Which frame and epoch each Vehicle, Orbit, Celestial and Camera member lives in, how to sample positions at one consistent instant and how to convert a position to latitude, longitude and altitude

# How-To

* [Equatorial versus ecliptic reference planes for inclination](understand-ksa-coordinate-frames-and-epochs-4.md) - Which plane the stock Orbit.Inclination is measured against, how to get the other plane's normal in CCI, and how a plane-change tool picks its reference
* [Pair the rendered Ego position with ECL offsets from the same instant](understand-ksa-coordinate-frames-and-epochs-5.md) - Draw overlays beside a craft by pairing the position KSA renders it at with the ECL sample the geometry was measured against, which removes the 500 m per frame ecliptic error
* [Store anchors in the owner's frame and resolve them to ECL on use](understand-ksa-coordinate-frames-and-epochs-3.md) - Keep points attached to bodies, orbits, surfaces and parts as offsets in their owner's frame (CCE offset, body-fixed latitude and longitude, part-local asmb) and convert to ECL only when drawing or measuring
* [Use Ecl as the working frame, Ego for anything drawn, and cancel the 29.8 km/s carrier](understand-ksa-coordinate-frames-and-epochs-2.md) - Keep all simulation state in the ecliptic frame at one instant, move samples between instants only with an explicit carry, and use frame-invariant relative quantities such as closest approach so the planet's orbital motion cancels
* [Vessel state in CCI, CCF and ECL: which member is in which frame](understand-ksa-coordinate-frames-and-epochs-1.md) - Read a vehicle's position, velocity and attitude in the frame each Vehicle member actually returns, convert a position to latitude, longitude and altitude, and go back from latitude and longitude to a position
