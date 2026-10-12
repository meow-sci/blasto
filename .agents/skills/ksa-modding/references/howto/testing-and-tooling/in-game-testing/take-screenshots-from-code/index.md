# Game Feature

* [Take clean screenshots from code, with a composed camera shot held for the capture](overview.md) - Ask the game's own ScreenshotCapture for a UI-free frame, collect the file with a manifest of the instant, and hold the main view at a scripted pose until the warm-up frames settle

# How-To

* [A capture button that only sets a flag, with player bundles of frames, state and log tail](take-screenshots-from-code-5.md) - Offer screenshots to players by flagging the request in the UI pass and starting the capture on the next frame hook, saving a note and the log tail beside eight frames
* [A SceneCamera shot relative to the craft or a building, held by the dev bridge](take-screenshots-from-code-3.md) - The lean variant of a held composed shot with one pose source, an office_view command at building coordinates and a release that hands the original view back
* [Compose a shot with a held SceneCamera, a 24-frame countdown and a manifest of the craft's screen position](take-screenshots-from-code-2.md) - Hold the main view at an offset from the flown craft aimed at a target, wait out the warm-up frames and record where the craft falls in the picture
* [Hold the main view from the bridge as one camera claimant, optionally orbiting on player time](take-screenshots-from-code-4.md) - Make the bridge an IViewPose that takes the main view in claim order beside the mod's other camera owners and hands the original view back
* [Reflect Program._screenshotCapture once and call Request with a warm-up, then move the PNG beside a manifest](take-screenshots-from-code-1.md) - Request a UI-free screenshot from the frame postfix, find the file KSA writes, move it into a per-command folder and write a JSON manifest of the instant it shows
