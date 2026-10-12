# Game Feature

* [Build an external SDK client or autopilot for the game over files and HTTP](overview.md) - Wrap a mod's external API in a typed client with one Transport interface over files and HTTP, then fly a vessel from a separate process with sim-time pacing, telemetry freshness gates and atomic batch writes

# How-To

* [Typed SDK with a Transport interface, plus example autopilot programs](build-external-clients-and-autopilots-with-a-typed-sdk.md) - Build a client where a single Transport interface hides whether the game is reached through files or HTTP, then write a landing or guidance program that paces by sim time, gates on telemetry freshness and batches related writes
