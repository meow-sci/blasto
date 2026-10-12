# Game Feature

* [Host HTTP and MQTT servers in the game process for telemetry and control](overview.md) - Expose game state and accept commands from outside the game with a raw-socket HTTP server or an embedded MQTT broker that both read snapshots and write through the game-thread queue

# How-To

* [Embedded broker with subscription-gated changed-only publish and ProcessPublish=false command topic](host-http-and-mqtt-servers-in-the-game-process-2.md) - Embed an MQTTnet broker that publishes retained changed-only values while subscribers exist and consumes command topics without relaying them
* [Raw TcpListener server with SSE, long-poll, OpenAPI and a generic command endpoint](host-http-and-mqtt-servers-in-the-game-process-1.md) - Serve JSON snapshots, Server-Sent Events, a sim-time long-poll and one generic command endpoint from a hand-written HTTP/1.1 server inside the game process
