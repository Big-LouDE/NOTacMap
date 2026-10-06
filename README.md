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
- Every map color is editable under DISPLAY, from a honeycomb palette or with RGB and hex values
- Tap the legend to open or close it
- A dark outline on text keeps labels readable over the terrain (switch it off in DISPLAY)
- FILTER and DISPLAY (top right) control what's shown, how it looks, and the colors
- The background map comes from the game's own terrain texture, so any map works
- Pan, zoom, and a button to recenter on you
- Optional LAN mode to open the map on a phone or tablet on your home network. Off by default, touch works
- Light on frame rate: redraws slowly when idle, speeds up when you pan or zoom, and sleeps while the tab is hidden

## Possible add-ons

Sharing marked targets between teammates who both have the mod. The game doesn't send your target selection to anyone else, so the two copies would have to talk to each other directly. If it happens, it'll be a separate optional add-on.

## How it works

The plugin (`NOTacMap.dll`) runs inside the game and starts a local HTTP server (`http://localhost:8123/` by default). Only this PC can reach it, unless you turn on LAN access (see Phone or tablet). It only reads data your client already has, and never asks the game server for anything extra or sends networked commands. Marked targets and locks only show what the game's own tracker knows. Other websites can't read it or change your settings. The page (`web/index.html`) is a single HTML/JS file that connects over Server-Sent Events and draws on a canvas.

## Installation

NOTacMap is listed on [NOMNOM](https://github.com/KopterBuzz/NOMNOM), the community registry used by mod managers like [NOMM](https://github.com/Combat787/NuclearOptionModManager). Search for it there and install. (NOMNOM is a community project, not affiliated with Shockfront Studios.)

To install by hand, download the latest release from the [Releases page](https://github.com/Big-LouDE/NOTacMap/releases) and extract the `NOTacMap` folder into `BepInEx/plugins/` in your Nuclear Option install. Requires BepInEx 5.

Either way, the map page opens in your default browser when the game starts. Drag it to your second monitor. To stop that, set `Server > AutoOpenBrowser` to false in the BepInEx config and open `http://localhost:8123/` yourself.

To build from source:

1. Requires the .NET 8 SDK and a local copy of Nuclear Option with BepInEx 5 installed.
2. `dotnet build NOTacMap.csproj -c Release` (pass `-p:GameDir=<path>` if your game isn't in the default Steam folder).
3. Copy `NOTacMap.dll` and `Newtonsoft.Json.dll` from the build output, and `index.html` from the `web/` folder, into `BepInEx/plugins/NOTacMap/` in your game folder. All three go in that one folder next to each other. Don't copy the `web/` folder itself, or the page won't load.

## Phone or tablet

Off by default. To open the map on another device on your home network:

1. Set `Server > AllowLan` to `true` in `BepInEx/config/com.bigloude.notacmap.cfg` and start the game.
2. With LAN on, the map page on your PC first asks where you want to see the map. Pick "On another device" and it shows a QR code and the link. Open the link, or scan the code with the camera, on the other device once. The phone remembers the link afterwards.
3. If Windows asks about the firewall, allow it on private networks only.

Tick "Remember my choice" to skip the question next time. DISPLAY > Phone / tablet changes it later and brings the QR code back. While the PC tab shows the QR code it draws no map, so the plugin sends nothing until a device connects.

Only devices on a private network address (192.168.x.x, 10.x.x.x, 172.16 to 31.x.x or 169.254.x.x) are accepted, and they need the secret in the link. Delete `LanToken` from the config and restart the game to get a new link and cut off old ones.

Anyone who has the link can see your map, and the connection isn't encrypted, so use it at home and not on public wifi. Saving settings only works from the PC, so a phone keeps its own display settings.

If Windows won't let the game listen on the network, the plugin says so in the log and the map keeps working on the PC. The page can't keep a phone screen awake over plain HTTP, so set a longer screen timeout on the phone.

## Configuration

All settings are on screen. **FILTER** covers unit types, weapon categories, overlays, and trails. **DISPLAY** covers text size, redraw rate, pilot names, dark mode, and colors. Both save to `settings.json` next to the plugin.

## About this project

Claude helped out here and there while I was building this, mostly to make sense of how the game's code works.

## Credits / third-party components

- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1): mod loader
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (MIT): JSON serialization
- [Mirage](https://github.com/MirageNet/Mirage) (MIT): referenced read-only against the game's own networking types, not redistributed

No in-game assets are bundled. The background map is captured from the game's own terrain texture on your machine each time the plugin loads, and is never stored.

## License

MIT, see [LICENSE](LICENSE).
