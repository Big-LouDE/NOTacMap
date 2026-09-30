# NOTacMap

A lean, second-screen tactical map for [Nuclear Option](https://store.steampowered.com/app/2168680/Nuclear_Option/). A BepInEx plugin reads live game state and serves it to a browser page you open on a second monitor — no manual calibration, no per-map setup.

## Features

- Opens itself automatically in your default browser as soon as the game starts (togglable) — no URL to remember or type
- Live player position, tracked units, airbases, and waypoints
- Unit-type shapes — planes, helicopters, tanks, APCs, radar, AA/SAM, ships, and buildings each get a distinct icon (with a fallback for anything not yet catalogued), plus an on-screen legend. Classification is name-based against a verified keyword list (vanilla units and installed mods alike — nothing guessed), falling back to a plain C# type check for anything unrecognized
- Heading rotation where it's actually meaningful: your own plane icon, missiles/bombs in flight, and your own helicopter icon (shown as its real glyph with a small heading tick, not a misleading plane chevron) — ground vehicles and other static/symmetric shapes intentionally don't rotate, since their heading is either invisible at that shape or too noisy to mean anything
- Friend (blue) / NPC (green) / enemy (red) coloring
- Incoming-missile warnings gated on the same RWR detection the game's own cockpit uses, with real speed and ETA
- Flight-wide weapon tracking: every friendly missile in flight shows, not just yours, attributed to its pilot (toggle between real names and randomly-assigned callsigns), rendered with its own distinct shape
- Your own marked target(s) show with a distinct amber reticle before you even fire — separate from the pink lock line a weapon in flight gets
- On-screen FILTER and DISPLAY buttons (top-right, below recenter): toggle any unit-type or weapon category on/off individually, and set the text size (Small/Normal/Large/X-Large). Saved to a `settings.json` next to the plugin, so it's remembered across game restarts — no URL editing needed
- Live-captured background map image, straight from the game's own terrain texture — works for any map automatically, no calibration step
- Free pan/zoom with a recenter-on-me control
- Night mode (`?theme=dark`)

## Possible add-ons

Sharing marked targets between teammates who both have the mod installed. The game itself only tells your own client what you've selected as a target before you actually fire - it never sends that to anyone else. So the two copies of the mod would have to talk to each other directly for this to work at all. If it happens, it'd be its own separate optional add-on rather than something baked into the base mod, so everyone else keeps the plain single-player, no-setup version they already have.

## How it works

The plugin (`NOTacMap.dll`) runs inside the game process and starts a local HTTP server (`http://localhost:8123/` by default) that only ever reads data already synced to your own client — it never requests anything extra from the game server and never sends networked commands. The page it serves (`web/index.html`) is a self-contained HTML/JS file that connects over Server-Sent Events and draws everything on a canvas.

## Installation

Grab the latest release from the [Releases page](https://github.com/Big-LouDE/NOTacMap/releases) and extract the `NOTacMap` folder into `BepInEx/plugins/` in your Nuclear Option install. Requires BepInEx 5. Launch the game — the map page opens automatically in your default browser (drag it to your second monitor). Turn this off in the BepInEx config (`Server > AutoOpenBrowser`) if you'd rather open `http://localhost:8123/` yourself.

Newer features that aren't in the stable release yet (customizable colors, runway approach guides, a weapon range ring, and more) are available in pre-release test builds on the same Releases page, while they're still being tried out before becoming the default version.

To build from source instead:

1. Requires the .NET 8 SDK and a local copy of Nuclear Option with BepInEx 5 installed.
2. `dotnet build NOTacMap.csproj -c Release` (pass `-p:GameDir=<path>` if your game isn't at the default path referenced in the `.csproj`).
3. Copy `NOTacMap.dll`, `Newtonsoft.Json.dll`, and `web/index.html` into `BepInEx/plugins/NOTacMap/` in your game folder.

## Configuration

Most of this is now on-screen: click **FILTER** to toggle unit-type/weapon categories, or **DISPLAY** to change text size. Both persist to `settings.json` next to the plugin automatically — nothing to type.

A couple of settings are still URL-only (append to the page address):

| Param | Values | Default | Effect |
|---|---|---|---|
| `names` | `real`, `callsign` | `callsign` | Show teammates' real names, or a randomly-assigned callsign |
| `theme` | `dark` | (unset) | Red-tinted night mode |

`?weapons=` still works too, but only as a one-off override for that page load — it's never saved, so it can't override what you've actually set in the FILTER panel beyond that one load.

## Credits / third-party components

- [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) — the mod loader this plugin runs under
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) (MIT) — JSON serialization
- [Mirage](https://github.com/MirageNet/Mirage) (MIT) — referenced read-only against the game's own networking types; not redistributed

No in-game assets, textures, or models are bundled with or redistributed by this project. The background map image is captured live from the game's own `MapSettings.TerrainColorMap` at runtime, on the user's own machine, each time the plugin loads — it is never stored in this repository or shipped in a release.

## License

MIT — see [LICENSE](LICENSE).
