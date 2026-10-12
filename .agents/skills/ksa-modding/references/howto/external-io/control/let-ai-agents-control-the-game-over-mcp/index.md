# Game Feature

* [Let AI agents drive the game through an MCP server](overview.md) - Expose a running KSA game to a coding agent as Model Context Protocol tools, either from an in-process Streamable HTTP endpoint or from a small stdio server that wraps a file bridge and returns screenshots inline

# How-To

* [Stateless Streamable HTTP MCP endpoint inside the game process](let-ai-agents-control-the-game-over-mcp-1.md) - Host the official MCP C# SDK behind a hand-rolled HTTP adapter in the mod, map tools to the command queue and reads to immutable snapshots, and shape the tool surface for a language-model client
* [Stdlib Python stdio MCP server wrapping the file bridge, with image tools](let-ai-agents-control-the-game-over-mcp-2.md) - Register a dependency-free Python MCP server for the repository that turns each tool call into a bridge command file for the mod and returns screenshots inline as base64 JPEG
