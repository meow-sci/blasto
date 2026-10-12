# Game Feature

* [Convert a click on a planet to latitude and longitude, measure between points and draw the great-circle arc](overview.md) - Ray-test the cursor against each body's mean sphere, convert the hit to latitude and longitude through the body-fixed frame, measure great-circle distance and bearing, and draw the arc only on the visible hemisphere

# How-To

* [Draw a great-circle arc over a planet without drawing through it: 48 Rodrigues samples with a back-face test](click-a-planet-to-get-latitude-and-longitude-2.md) - Sample a great circle between two surface points with Rodrigues rotation, interpolate the radius, and draw each segment only while it faces the camera so the line breaks at the limb
* [Sphere pick, GetLatitudeFromCce and GetLongitudeFromCce, haversine, chord and bearing](click-a-planet-to-get-latitude-and-longitude-1.md) - Pin two points on a planet from the cursor with a sphere ray test, store them as latitude and longitude, and compute great-circle distance, chord and initial bearing
