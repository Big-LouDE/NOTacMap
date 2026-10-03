# NOTacMap

A live tactical map for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/), for a second monitor. A BepInEx plugin reads game state and serves it to a page in your browser. Nothing to calibrate or set up per map.

## Features

- Opens in your browser when the game starts (can be turned off), and closes itself when the game does if the browser allows it
- Double-click the map for fullscreen
- Live position, tracked units, airbases, waypoints
- Icons per unit type (planes, helicopters, tanks, APCs, radar, AA/SAM, ships, buildings) with a legend. Units are matched by name, mods included. Anything unknown gets a fallback shape
- Icons rotate for your aircraft, helicopter, missiles and bombs
- Teammates, AI/structures, and enemies each get their own color
- Incoming-missile warnings with speed and ETA, from the same RWR detection as the cockpit
- Friendly missiles in flight are shown for everyone, labeled with the pilot
- Marked targets get an amber reticle, which turns into a spinning "LOST" marker if you lose contact
- Missile locks show as a pink line and ring. With several missiles out, the closest one gets the label and the rest are small rings (+N)
- If a missile loses its lock on an aircraft, a ring shows where it was and the line snaps
- Weapon range ring: a circle at your weapon's base range that turns into a cone along your heading once you mark a target. The range matches the cockpit HUD
- Runway approach lines extending past each runway end
- Fading trails behind moving units, with a Trails folder in FILTER to pick which kinds. The grid can be switched off too
- Ejected pilots are hidden by default
- Every map color is editable under DISPLAY
- FILTER and DISPLAY (top right) control what's shown, text size, pilot names, dark mode, and colors. Saved to a `settings.json` next to the plugin
- The background map comes from the game's own terrain texture, so any map works
- Pan, zoom, and a button to recenter on you

## Possible add-ons

Sharing marked targets between teammates who both have the mod. The game doesn't send your target selection to anyone else, so the two copies would have to talk to each other directly. If it happens, it'll be a separate optional add-on.

## How it works

The plugin (`NOTacMap.dll`) runs inside the game and starts a local HTTP server (`http://localhost:8123/` by default). It only reads data your client already has, and never asks the game server for anything extra or sends networked commands. Marked targets and locks only show what the game's own tracker knows. The page (`web/index.html`) is a single HTML/JS file that connects over Server-Sent Events and draws on a canvas.

## Installation

NOTacMap is listed on [NOMNOM](https://github.com/KopterBuzz/NOMNOM), the community registry used by mod managers like [NOMM](https://github.com/Combat787/NuclearOptionModManager). Search for it there and install. (NOMNOM is a community project, not affiliated with Shockfront Studios.)

To install by hand, download the latest release from the [Releases page](https://github.com/Big-LouDE/NOTacMap/releases) and extract the `NOTacMap` folder into `BepInEx/plugins/` in your Nuclear Option install. Requires BepInEx 5.

Either way, the map page opens in your default browser when the game starts. Drag it to your second monitor. To stop that, set `Server > AutoOpenBrowser` to false in the BepInEx config and open `http://localhost:8123/` yourself.

To build from source:

1. Requires the .NET 8 SDK and a local copy of Nuclear Option with BepInEx 5 installed.
2. `dotnet build NOTacMap.csproj -c Release` (pass `-p:GameDir=<path>` if your game isn't at the default path in the `.csproj`).
3. Copy `NOTacMap.dll` and `Newtonsoft.Json.dll` from the build output, and `index.html` from the `web/` folder, into `BepInEx/plugins/NOTacMap/` in your game folder. All three go in that one folder next to each other. Don't copy the `web/` folder itself, or the page won't load.

## Configuration

All settings are on screen. **FILTER** covers unit types, weapon categories, overlays, and trails. **DISPLAY** covers text size, pilot names, dark mode, and colors. Both save to `settings.json` next to the plugin.

## About this project

Claude helped out here and there while I was building this, mostly to make sense of how the game's code works.

## Credits / third-party components

- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1): mod loader
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (MIT): JSON serialization
- [Mirage](https://github.com/MirageNet/Mirage) (MIT): referenced read-only against the game's own networking types, not redistributed

No in-game assets, textures, or models are bundled or redistributed. The background map image is captured from the game's own `MapSettings.TerrainColorMap` at runtime on the user's machine, each time the plugin loads. It's never stored in this repository or shipped in a release.

## License

MIT, see [LICENSE](LICENSE).
