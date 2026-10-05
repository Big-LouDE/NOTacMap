# NOTacMap

A live tactical map for Nuclear Option, for a second monitor. A BepInEx plugin reads game state and serves it to a page in your browser. Nothing to calibrate or set up per map.

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
- Every map color is editable under DISPLAY
- FILTER and DISPLAY (top right) control what's shown, how it looks, and the colors. Saved to a `settings.json` next to the plugin
- The background map comes from the game's own terrain texture, so any map works
- Pan, zoom, and a button to recenter on you
- Optional LAN mode to open the map on a phone or tablet on your home network. Off by default, touch works
- Light on frame rate: redraws slowly when idle, speeds up when you pan or zoom, and sleeps while the tab is hidden

## Install

Install with r2modman or the Thunderstore app. The map page opens in your default browser when the game starts. Drag it to your second monitor. To stop that, set `Server > AutoOpenBrowser` to false in the BepInEx config and open `http://localhost:8123/` yourself.

Requires BepInEx 5, which the mod manager installs for you.

## Phone or tablet

Off by default. To open the map on another device on your home network:

1. Set `Server > AllowLan` to `true` in `BepInEx/config/com.bigloude.notacmap.cfg` and start the game.
2. Open DISPLAY in the map page on your PC. A "Phone / tablet" link appears there. Open that link on the other device once, it remembers you afterwards.
3. If Windows asks about the firewall, allow it on private networks only.

Only devices on a private home address (192.168.x.x, 10.x.x.x, 172.16 to 31.x.x) are accepted, and they need the secret in the link. Anyone who has the link can see your map, and the connection isn't encrypted, so use it at home and not on public wifi. Delete `LanToken` from the config to get a new link and cut off old ones. Saving settings only works from the PC, so a phone keeps its own display settings.

If Windows won't let the game listen on the network, the plugin says so in the log and the map keeps working on the PC. The page can't keep a phone screen awake over plain HTTP, so set a longer screen timeout on the phone.

## How it works

The plugin runs inside the game and starts a local HTTP server (`http://localhost:8123/` by default). It only reads data your client already has, and never asks the game server for anything extra or sends networked commands. Marked targets and locks only show what the game's own tracker knows. Other websites can't read it or change your settings. The page is a single HTML/JS file that connects over Server-Sent Events and draws on a canvas.

## About this project

Claude helped out here and there while I was building this, mostly to make sense of how the game's code works.

Source and issues: https://github.com/Big-LouDE/NOTacMap

## Credits / third-party components

- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1): mod loader
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (MIT): JSON serialization
- [Mirage](https://github.com/MirageNet/Mirage) (MIT): referenced read-only against the game's own networking types, not redistributed

No in-game assets are bundled. The background map is captured from the game's own terrain texture on your machine each time the plugin loads, and is never stored.

## License

MIT
