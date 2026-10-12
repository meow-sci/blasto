---
type: How-To
title: Return contact sheets, GIFs and temporal-noise maps inline through an MCP server
description: Wrap the file bridge in a standard-library MCP server that launches the game only if needed and returns captures as base64 JPEG, sheets, GIFs and noise maps an agent can read
category: testing-and-tooling
group: in-game-testing
topic: visual-regression-for-mods
approach: 2
difficulty: advanced
prerequisites: [starmap, python, pillow, numpy]
demonstrated_by:
  - mods/ksacars/features/ksa-mcp-server
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksarmory/features/file-command-bridge-and-mcp
techniques: [mcp-server, image-diff]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame, thread:background]
game_types: [KSA.ScreenshotCapture]
tags: [mcp, visual-testing, python, contact-sheet, gif, agent]
---

Outcome: an agent (or you from a shell) calls `ksa_capture` and gets the picture back in the conversation: one frame as an inline JPEG, a series as a labelled contact sheet plus a GIF path plus a
temporal-noise map, and variants as pictures with their diffs. Demonstrated by KSACars ([KSA MCP server](../../../../mods/ksacars/features/ksa-mcp-server.md)), with the same server shape in
KSAStructures, KSAGolf and KSArmory. Prerequisites: the file bridge and capture manifests from the earlier articles, Python 3, Pillow and numpy, an MCP client (the repos register the server in `.mcp.json`).
Letting an agent do more than screenshots over MCP has no written topic yet.

## Background: how the game does it

The game is not involved beyond the bridge: the server reads and writes the `in/` and `out/` folders the mod polls ([bridge article 1](../drive-a-running-game-through-a-file-bridge/drive-a-running-game-through-a-file-bridge-1.md)) and the images the capture command moved there
([take screenshots, article 1](../take-screenshots-from-code/take-screenshots-from-code-1.md)). MCP is JSON-RPC over stdin and stdout, one message per line, so a server needs no dependency; the images are what need Pillow and numpy.
Under WSL the mod writes Windows paths into its replies, so the server converts `C:\...` to `/mnt/c/...` before opening images.

## Lifecycle and timing

Entirely outside the game process. Each tool call becomes one bridge command that the mod runs from `StarMapAfterOnFrame` (`frame:after-on-frame`), one at a time; the server is synchronous to match the single queue, with timeouts scaled to the request
(`60 + frames * 10` seconds for a series in KSAGolf). The PNG encode is KSA's background task (`thread:background`). See the [frame order](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Register the server** in the repository's `.mcp.json` so a client in that directory starts it over stdio:

   ```json
   // .mcp.json L1-L8
   {
     "mcpServers": {
       "ksa": {
         "command": "python3",
         "args": ["tools/ksa-mcp/server.py"]
       }
     }
   }
   ```

2. **Speak minimal MCP.** `initialize` echoes the client's protocol version and advertises tools, `ping`, `tools/list` and `tools/call`; anything else gets `-32601`; notifications (no `id`) are ignored:

   ```python
   # tools/ksa-mcp/server.py L370-L405
   def serve() -> None:
       def reply(ident, result=None, error=None):
           msg = {"jsonrpc": "2.0", "id": ident}
           msg["result" if error is None else "error"] = result if error is None else error
           sys.stdout.write(json.dumps(msg) + "\n")
           sys.stdout.flush()

       for line in sys.stdin:
           line = line.strip()
           if not line:
               continue
           try:
               msg = json.loads(line)
           except json.JSONDecodeError:
               continue

           method, ident = msg.get("method"), msg.get("id")
           if ident is None:
               continue  # a notification

           if method == "initialize":
               version = (msg.get("params") or {}).get("protocolVersion", "2025-06-18")
               reply(ident, {"protocolVersion": version, "capabilities": {"tools": {"listChanged": False}},
                             "serverInfo": {"name": "ksa", "version": "0.1"}})
           elif method == "ping":
               reply(ident, {})
           elif method == "tools/list":
               reply(ident, {"tools": [{"name": n, "description": t[0],
                                        "inputSchema": {"type": "object", "properties": t[1], "required": t[2]}}
                                       for n, t in TOOLS.items()]})
           elif method == "tools/call":
               params = msg.get("params") or {}
               content, is_error = call(params.get("name", ""), params.get("arguments") or {})
               reply(ident, {"content": content, "isError": is_error})
           else:
               reply(ident, error={"code": -32601, "message": f"no method {method}"})
   ```

3. **Define tools as data** (name, description, JSON schema, handler). The capture tool's description tells the agent what the arguments do, which matters more than the code:

   ```python
   # tools/ksa-mcp/server.py L336-L343
       "ksa_capture": ("Screenshot the game. frames>1 takes a series (every_s simulated seconds, or every_frames "
                       "rendered frames when paused) and returns a sheet, an animation path and a temporal-noise "
                       "map. variants=[{setting: value}] photographs each in the same paused instant as the base "
                       "and returns the diffs. crop=true crops round the craft being flown.",
                       {"label": {"type": "string"}, "frames": _num("count"), "every_s": _num("sim s"),
                        "every_frames": _num("frames"), "variants": {"type": "array", "items": {"type": "object"}},
                        "crop": {"type": "boolean"}, "width": _num("px")}, [],
                       lambda a: capture(**a)),
   ```

4. **Return pictures inline.** Images go out as base64 JPEG content, downscaled (960 px wide by default), with the manifest summarised as text beside them:

   ```python
   # tools/vis/vis.py L53-L56
   def jpeg_b64(img: Image.Image, width: int = 960, quality: int = 85) -> str:
       buf = io.BytesIO()
       fit(img, width).save(buf, "JPEG", quality=quality)
       return base64.b64encode(buf.getvalue()).decode("ascii")
   ```
   ```python
   # tools/ksa-mcp/server.py L224-L235
   def _brief(shot: dict) -> str:
       keys = ("label", "craft", "craft_screen", "fov_deg", "paused", "speed")
       return ", ".join(f"{k}={shot[k]}" for k in keys if k in shot)


   def _text(text: str) -> dict:
       return {"type": "text", "text": text}


   def _image(img, width: int) -> dict:
       return {"type": "image", "data": vis.jpeg_b64(img, width), "mimeType": "image/jpeg"}

   ```

5. **Turn a series into a sheet, a GIF and a noise map**, saved beside the images in the command's `out/<id>/` folder and returned as text paths plus an inline sheet and noise map:

   ```python
   # tools/ksa-mcp/server.py L185-L209
       data = send("capture", timeout=60 + frames * 10, label=label, frames=frames,
                   every_s=every_s, every_frames=every_frames)
       shots = data["frames"]
       imgs = [vis.load(wsl(s["file"])) for s in shots]

       if frames == 1:
           content.append(_text(f"{shots[0]['file']}\n{_brief(shots[0])}"))
           content.append(_image(vis.crop(imgs[0], shots[0]) if crop else imgs[0], width))
           return content

       folder = Path(wsl(data["folder"]))
       labels = [str(s.get('index', '?')) for s in shots]
       show = [vis.crop(i, s) if crop else i for i, s in zip(imgs, shots)]
       sheet_path = folder / "sheet.jpg"
       vis.sheet(show, labels, cols=min(4, len(show))).save(sheet_path, quality=88)
       gif_path = vis.gif(show, folder / "series.gif")
       t, stats = vis.temporal(imgs)
       t.save(folder / "temporal.png")

       content.append(_text(f"{frames} frames in {folder}\nsheet {sheet_path}\nanimation {gif_path}\n"
                            f"temporal {stats} -- with the world paused, bright is renderer noise\n"
                            + "\n".join(_brief(s) for s in shots)))
       content.append(_image(vis.load(sheet_path), 1280))
       content.append(_image(vis.crop(t, shots[0]) if crop else t, width))
       return content
   ```

   `sheet` lays the frames in a labelled grid and `gif` writes an animation ([tools/vis/vis.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/vis/vis.py#L96-L118)).

6. **Add a CLI mode** so the same tools work from a shell before the client has the MCP tools: the images an MCP client would see go to `tools/ksa-mcp/last/NN.jpg`:

   ```python
   # tools/ksa-mcp/server.py L408-L430
   def cli(argv: list[str]) -> int:
       name = argv[0] if argv[0].startswith("ksa_") else f"ksa_{argv[0]}"
       args = json.loads(argv[1]) if len(argv) > 1 else {}
       content, is_error = call(name, args)

       # What an MCP client would be shown inline, written out so a shell can look at it too.
       shown = Path(__file__).resolve().parent / "last"
       shutil.rmtree(shown, ignore_errors=True)
       shown.mkdir()
       for k, c in enumerate(content):
           if c["type"] == "text":
               print(c["text"])
           else:
               path = shown / f"{k:02d}.jpg"
               path.write_bytes(base64.b64decode(c["data"]))
               print(f"[image] {path}")
       return 1 if is_error else 0


   if __name__ == "__main__":
       if len(sys.argv) > 2 and sys.argv[1] == "cli":
           sys.exit(cli(sys.argv[2:]))
       serve()
   ```

7. **Do not take the player's game.** KSACars' server starts the game through `tools/run.sh` only if no `StarMap.exe` or KSA process is running (checked with `tasklist.exe`), records its PID, and `ksa_quit` closes only a game it launched; KSArmory's doc records why (a deploy had killed the player's session three times) ([docs/VISUAL-TESTING.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/VISUAL-TESTING.md)). A skill that tells the agent to ask before touching a game it did not start is in [.claude/skills/verify-in-game/SKILL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.claude/skills/verify-in-game/SKILL.md).

8. **Verify.** In a client, call `ksa_status`, then `ksa_capture` with `{"frames":4,"every_frames":1}`; expect `4 frames in <folder>`, `sheet <path>`, `animation <path>` and `temporal {...}` lines and two images.

## Complete example

| File | Role |
|---|---|
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/ksa-mcp/server.py) | Server, tools, capture post-processing, CLI |
| [.mcp.json](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.mcp.json) | Registration |
| [tools/vis/vis.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/vis/vis.py) | Image helpers |
| [tools/ksa-mcp/server.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/ksa-mcp/server.py) | The same server with car-specific tools (`ksa_drive`, `ksa_spawn`) |
| [.claude/skills/verify-in-game/SKILL.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/.claude/skills/verify-in-game/SKILL.md) | How an agent should use the tools as evidence before calling a change a fix |

## Variations and gotchas

- **Replies are small, pictures are not.** Always downscale before base64; a full-resolution PNG in a conversation is wasteful.
- **The CLI empties `last/` on every call.** Copy what you want to keep.
- **Two mods built from the same tooling share a bridge folder** (KSAGolf, KSArmory) unless the mod adds a subfolder (KSACars, KSAStructures); the server must point at the right one.
- **The server needs Pillow and numpy** even though the protocol part is stdlib.
- **Single queue.** Tools are serial; an agent cannot overlap a `step` with a `capture`.

## See also

- [Overview](overview.md), [article 1](visual-regression-for-mods-1.md)
- [Take clean screenshots from code](../take-screenshots-from-code/overview.md), [Drive a running game through a file bridge](../drive-a-running-game-through-a-file-bridge/overview.md)
- [KSACars KSA MCP server](../../../../mods/ksacars/features/ksa-mcp-server.md), [KSArmory file command bridge](../../../../mods/ksarmory/features/file-command-bridge-and-mcp.md)
