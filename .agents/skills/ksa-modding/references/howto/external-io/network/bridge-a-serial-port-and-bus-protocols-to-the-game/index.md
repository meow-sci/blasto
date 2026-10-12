# Game Feature

* [Bridge a serial port and bus protocols (NDJSON, NMEA, CCSDS, SCPI) to the game](overview.md) - Connect a virtio-serial port to the game with NDJSON, NMEA or CCSDS telemetry frames and SCPI-style command lines answered with OK or ERR plus an errno name

# How-To

* [SerialBridge with a connector per boot, protocol encoders and a SCPI command port](bridge-a-serial-port-and-bus-protocols-to-the-game.md) - Bridge a QEMU virtio-serial chardev to the game by streaming NDJSON, NMEA or CCSDS telemetry and answering SCPI-style command lines with OK or ERR plus an errno name
