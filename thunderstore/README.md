# NOTacMap

A live tactical map for Nuclear Option, for a second monitor. A BepInEx plugin reads game state and serves it to a page in your browser. Nothing to calibrate or set up per map.

A second-screen map for HOTAS pilots. It follows your aircraft by itself, so you never have to reach for the mouse. Works in multiplayer. Tested on official and community servers. It only shows what your own game already knows and sends nothing to the server.

## Features

- Opens in your browser when the game starts (can be turned off), and closes itself when the game does if the browser allows it. Double-click the map for fullscreen
- Live position, tracked units, airbases and waypoints, with an icon per unit type and a legend. Units are matched by name, mods included, and anything unknown gets a fallback shape. Icons rotate for your aircraft, helicopter, missiles and bombs
- Teammates, AI/structures and enemies each get their own color, and every map color is editable under DISPLAY, from a honeycomb palette or with RGB and hex values
- Incoming-missile warnings with speed and ETA, from the same RWR detection as the cockpit. Friendly missiles in flight are shown for everyone, labeled with the pilot
- Marked targets get an amber reticle, which turns into a spinning "LOST" marker if you lose contact
- Missile locks show as a pink line and ring. With several missiles out, the closest one gets the label and the rest are small rings (+N). If a missile loses its lock on an aircraft, a ring shows where it was and the line snaps
- Weapon range ring: a circle at your weapon's base range that turns into a cone along your heading once you mark a target. The range matches the cockpit HUD
- Runway approach lines, and fading trails behind moving units. FILTER picks which kinds of trails show, and the grid can be switched off too
- FILTER and DISPLAY (top right) control what's shown, how it looks, and the colors. Saved to a `settings.json` next to the plugin. A dark outline on text keeps labels readable over the terrain (switch it off in DISPLAY). Tap the legend to open or close it, and the Help button next to it explains the symbols and the settings. The map shows teammates' real Steam names by default. If you stream, turn on callsigns under DISPLAY ("Use real names instead of callsigns").
- The background map comes from the game's own terrain texture, so any map works. Pan, zoom, and a button to recenter on you
- Optional LAN mode to open the map on a phone or tablet on your home network. Off by default, touch works
- Light on frame rate: redraws slowly when idle, speeds up when you pan or zoom, and sleeps while the tab is hidden

Want the details? [FEATURES.md](https://github.com/Big-LouDE/NOTacMap/blob/master/FEATURES.md) explains each feature and setting.

## Install

Install with r2modman or the Thunderstore app. The map page opens in your default browser when the game starts. Drag it to your second monitor. To stop that, set `Server > AutoOpenBrowser` to false in the BepInEx config and open `http://127.0.0.1:8123/` yourself. Use that address and not `localhost`: some browsers (Firefox often) reach `localhost` over IPv4, and the game's web server then answers "Bad Request (Invalid host)".

Requires BepInEx 5, which the mod manager installs for you.

## Phone or tablet

Off by default. To open the map on another device on your home network:

1. Start the game. The map page on your PC first asks where you want to see the map. Pick "On another device".
2. The first time, a prompt says what that opens up. Confirm it, then press "Show QR code and link". Open the link, or scan the code with the camera, on the other device once. The phone remembers the link afterwards.
3. If Windows asks about the firewall, allow it on private networks only.

"Remember my choice" is ticked by default, so you are only asked the first time. Untick it if you want to be asked every time.

Once access from other devices is on, it stays on after a restart until you switch it off. Picking "On this PC" does not turn it off. The DEVICES button (top right) has the switch and the setting for what the page does when it opens. The QR code and the link stay hidden until you press "Show QR code and link", and hide again after 2 minutes. While the PC tab is on that screen it draws no map and keeps no connection open. You can also set `Server > AllowLan` in the config; the switch keeps that value in step.

Only devices on a private network address (192.168.x.x, 10.x.x.x, 172.16 to 31.x.x or 169.254.x.x) are accepted, and they need the secret in the link. The New link button in DEVICES makes a new link and cuts off every device that has the old one. Deleting `LanToken` from the config and restarting the game does the same. Switching access off and on again keeps the same link.

Anyone who has the link can see your map, and the connection isn't encrypted, so use it at home and not on public wifi. Don't forward the port (router port forwarding, WSL2 or `netsh portproxy`, a reverse proxy): anything forwarded to this PC looks like it comes from the PC itself and needs no secret. Saving settings only works from the PC, so a phone keeps its own display settings.

If Windows won't let the game listen on the network, the page and the log say so, and the map keeps working on the PC.

**Keeping the screen on.** The page can't do it, so set a longer screen timeout on the phone. On Android you can also turn on "Stay awake" in the developer options, which keeps the screen on while it charges. On an iPhone, set Auto-Lock to Never. The reason: a web page may only keep a screen awake over HTTPS, and this map is plain HTTP on your home network.

## How it works

The plugin runs inside the game and starts a local HTTP server (`http://127.0.0.1:8123/` by default). Only this PC can reach it, unless you turn on LAN access (see Phone or tablet). It only reads data your client already has, and never asks the game server for anything extra or sends networked commands. Marked targets and locks only show what the game's own tracker knows. Other websites can't read it or change your settings. The page is a single HTML/JS file that connects over Server-Sent Events and draws on a canvas.

## Troubleshooting

**The page says "Bad Request (Invalid host)".** Open `http://127.0.0.1:8123/` and not `localhost`. Some browsers (Firefox often) reach `localhost` over a route the game's web server doesn't answer. Since 1.2.2 the mod opens the right address by itself, so this only happens if you typed `localhost` or run an older version.

**The page doesn't load at all.** Check that the game is running. If you changed the port in the config, restart the game and use the new port. If something else already uses the port (the NOX mod uses 8111), pick a free one with `Server > Port` in `BepInEx/config/com.bigloude.notacmap.cfg`. The game has to be restarted for a new port to apply.

**The page loads but says "disconnected, retrying".** The game closed, or it is still starting up. The page connects by itself as soon as the game is back.

**The phone can't open the link.** Both devices need to be on the same wifi, and the access switch under DEVICES has to be on. If Windows asks about the firewall, allow it on private networks. The link changes when the PC changes network, and "New link" cuts off the old one, so open DEVICES and scan the current code again.

**Where is the log?** In `BepInEx/LogOutput.log` in the game folder. The lines with NOTacMap say what the mod is doing and why a request was turned away. It never contains your phone link.

## Feedback and bugs

Found a bug? Open an issue on GitHub: https://github.com/Big-LouDE/NOTacMap/issues (it needs a free GitHub account). Say what you did and what you expected. If something broke, add the NOTacMap lines from `BepInEx/LogOutput.log`.

Have an idea or a question? Start a discussion on the [Discussions page](https://github.com/Big-LouDE/NOTacMap/discussions). If you found a security problem, please report it privately, as described in [SECURITY.md](https://github.com/Big-LouDE/NOTacMap/blob/master/SECURITY.md). If you want to change the code, read [CONTRIBUTING.md](https://github.com/Big-LouDE/NOTacMap/blob/master/CONTRIBUTING.md) first.

## About this project

Claude helped out here and there while I was building this, mostly to make sense of how the game's code works.

Source: https://github.com/Big-LouDE/NOTacMap

## Credits / third-party components

- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1): mod loader
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (MIT): JSON serialization
- [Mirage](https://github.com/MirageNet/Mirage) (MIT): referenced read-only against the game's own networking types, not redistributed

No in-game assets are bundled. The background map is captured from the game's own terrain texture on your machine each time the plugin loads, and is never stored.

## License

MIT
