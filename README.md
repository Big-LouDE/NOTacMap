# NOTacMap

A live tactical map for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/), meant for a second monitor. A BepInEx plugin reads game state and serves it to a page in your browser. Nothing to calibrate or set up per map.

## Features

- Opens in your default browser when the game starts (can be turned off)
- Live position, tracked units, airbases, waypoints
- Per-type icons (planes, helicopters, tanks, APCs, radar, AA/SAM, ships, buildings) with an on-screen legend. Classification is by unit name and covers vanilla and mod units. Anything unknown gets a fallback shape
- Heading rotation only for your aircraft, missiles and bombs in flight, and your helicopter. Ground units stay fixed, their heading is too noisy to be useful
- Separate colors for teammates, AI/structures, and enemies, all editable
- Incoming-missile warnings with speed and ETA, using the same RWR detection as the cockpit
- All friendly missiles in flight, not just yours, labeled with the pilot (real name or random callsign)
- Your marked targets get an amber reticle before you fire. Once a weapon is in flight it gets the pink lock line
- Weapon range ring around your aircraft: a full circle at the selected weapon's base range, which becomes a cone along your heading once you mark a target. The range matches the cockpit HUD
- Runway approach lines extending past each runway end, for lining up a landing from far out
- Ejected pilots are hidden by default (toggle in FILTER)
- Every map color is editable under DISPLAY, with RGB inputs, a preview, and a reset button
- FILTER and DISPLAY buttons (top right) control what's shown, text size, pilot names, dark mode, and colors. Saved to a `settings.json` next to the plugin
- Background map comes from the game's own terrain texture. Works on every map
- Pan, zoom, and a button to recenter on you

## Possible add-ons

Sharing marked targets between teammates who both have the mod. The game doesn't send your target selection to anyone else, so the two copies would have to talk to each other directly. If it happens, it'll be a separate optional add-on.

## How it works

The plugin (`NOTacMap.dll`) runs inside the game and starts a local HTTP server (`http://localhost:8123/` by default). It only reads data your client already has, never asks the game server for anything extra, and never sends networked commands. The page it serves (`web/index.html`) is a single HTML/JS file that connects over Server-Sent Events and draws on a canvas.

## Installation

NOTacMap is listed on [NOMNOM](https://github.com/KopterBuzz/NOMNOM), the community registry used by mod managers like [NOMM](https://github.com/Combat787/NuclearOptionModManager). Search for it there and install. (NOMNOM is a community project, not affiliated with Shockfront Studios.)

To install by hand, download the latest release from the [Releases page](https://github.com/Big-LouDE/NOTacMap/releases) and extract the `NOTacMap` folder into `BepInEx/plugins/` in your Nuclear Option install. Requires BepInEx 5.

Either way, the map page opens in your default browser when the game starts. Drag it to your second monitor. To stop that, set `Server > AutoOpenBrowser` to false in the BepInEx config and open `http://localhost:8123/` yourself.

To build from source:

1. Requires the .NET 8 SDK and a local copy of Nuclear Option with BepInEx 5 installed.
2. `dotnet build NOTacMap.csproj -c Release` (pass `-p:GameDir=<path>` if your game isn't at the default path in the `.csproj`).
3. Copy `NOTacMap.dll`, `Newtonsoft.Json.dll`, and `web/index.html` into `BepInEx/plugins/NOTacMap/` in your game folder.

## Configuration

All settings are on screen. **FILTER** covers unit types, weapon categories, and overlays. **DISPLAY** covers text size, pilot names, dark mode, and colors. Both save to `settings.json` next to the plugin.

## Credits / third-party components

- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1): mod loader
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (MIT): JSON serialization
- [Mirage](https://github.com/MirageNet/Mirage) (MIT): referenced read-only against the game's own networking types, not redistributed

No in-game assets, textures, or models are bundled or redistributed. The background map image is captured from the game's own `MapSettings.TerrainColorMap` at runtime on the user's machine, each time the plugin loads. It's never stored in this repository or shipped in a release.

## License

MIT, see [LICENSE](LICENSE).
