# NOTacMap features

This is the long version of the feature list. The README has the short one. It describes version 1.3.0.

NOTacMap puts a live map of your game on a second monitor, a phone or a tablet. A plugin inside Nuclear Option reads what your own side already knows, and a small web server inside the game hands it to a page in your browser. You don't set anything up per map.

---

## What it shows, and what it leaves out

Everything on the map comes from your faction's tracking data, the same source the game's own map uses. If your side hasn't spotted a unit, it isn't there. A unit you spotted and then lost stays where it was last seen, and it stops turning too, the same as on the game's own map.

The mod never asks the game server for anything and never sends commands over the network. It also can't see your teammates' marked targets, because the game keeps your target selection on your own machine.

The incoming-missile warning on the map (the blinking needle and its box) only appears after your cockpit warning has picked the missile up. The map can't know about a launch earlier than your RWR does.

Some things on the map go further than your cockpit does, like the speed and ETA of an incoming missile or who fired it. If you want only what the cockpit shows, switch on Fair play at the top of DISPLAY. It hides the speed and ETA in the missile warning, enemy missiles your warning hasn't picked up yet, the ATTACKER mark, the RADAR DESTROYED tag and the rings of enemy nuclear missiles, in the air and where they went off. Everything else you switched on stays as it is.

---

## Starting and stopping

When the game starts, the map opens in your default browser at `http://127.0.0.1:8123/`. If you'd rather open it yourself, set `Server > AutoOpenBrowser` to false in the BepInEx config. I use `127.0.0.1` and not `localhost` on purpose. Some browsers, Firefox most often, reach `localhost` by a route the game's web server doesn't answer, and you get "Bad Request (Invalid host)".

When the game closes, the page notices after three failed tries and closes its own tab. That also works while the page is still showing the first question or the phone link, where nothing is connected: it asks the game every three seconds instead. Browsers only allow that for a tab that was opened from a link or a launcher. If yours says no, the status line tells you the game is closed and you can close the tab yourself. You can turn this off under DISPLAY.

If the game is still loading, or a mission is changing, the page says "disconnected, retrying..." and picks up again on its own.

Double-click the map for fullscreen. F11 and Esc work as usual. There's also a fullscreen button, but only where the browser supports it, so not on iPhone Safari.

---

## Looking around the map

North is up by default. Under DISPLAY, Map up can be set to Your heading, and the map then turns so that you always fly up the screen. A small compass shows where north is, and the button under RECENTER ON ME switches between the two. Drag to pan, scroll the mouse wheel to zoom. The wheel zooms in smooth steps, about 10 percent for a normal notch. While the view follows you, you stay in the center. Once you have panned, the zoom goes toward the cursor and the point under it stays put. On a touch screen one finger pans and two fingers pinch to zoom, around the point between your fingers, and while the map follows you the pinch keeps you in the center. If you lift one finger of a pinch, you carry on panning with the other. Zoom goes from 0.5 to 200 meters per pixel and starts at 35.

The view follows your aircraft until you drag it. After that it stays where you put it, and the "recenter on me" button puts it back on you.

The background is the game's own terrain picture, taken once per map on your machine and never saved. The sea is painted in the Water color, a deep blue unless you pick another one under DISPLAY, Colors. Where the sea is comes from the game's own ocean depth map, so it follows the coast even on maps where the sea has a different color in different places. A map without a depth map falls back to the color of its four corners. Past the edge of the map the water carries on and gets darker the further out you go, so zooming all the way out shows a map in the middle of the sea. This works on any map, modded ones too, because it only needs the map size from the game.

The grid has a heavy line every 10 km and a thin line every 1 km once you're zoomed in far enough. Columns have numbers and rows have letters along the edge. You can switch the grid off in FILTER.

FILTER, under Overlays, also has distance rings and a scale bar. The rings are off by default. They are circles around you at 5, 10, 20 and 40 km (2, 5, 10 and 20 nautical miles in feet mode), and "Which rings" under DISPLAY, View can show just one of them. "Show scale" draws a scale bar in the bottom left that follows the zoom.

In the top right corner there's a grid reference like the one in the cockpit, for example `Hg95`. It's always for your own position, even when you've panned somewhere else. The numbers count from an offset that every map sets for itself, and the mod reads it from the game, so the reference matches the cockpit's.

You are a white arrow pointing along your heading. In a helicopter you're the helicopter symbol with a short line for the heading, since a rotor cross has no nose. Under you there's a small block with speed in km/h, altitude in meters and heading in degrees.

Labels try not to sit on top of each other. Things that are closer and more dangerous get their spot first, and the speed block is reserved before everything else.

---

## Units, airbases and waypoints

Every unit gets a symbol for its type. Planes are an arrow that turns with the heading. Helicopters are a circle with a rotor cross, tanks a filled square, APCs a filled diamond, radars a half circle with a line, AA and SAM sites a cross with a diagonal cross over it, ships a hollow diamond, other vehicles a hollow square and buildings a dot. An ejected pilot is an X, and missiles are a thin needle. The legend in the top left lists all of them.

The type comes from the unit's name first. I keep lists of names for the vanilla aircraft and vehicles and for the mods I've installed. When a name isn't in the lists, the game's own base type decides, so a new modded aircraft still shows up as a plane and a new vehicle as a vehicle. Anything that fits nothing gets the plane arrow and counts under "Plane" in FILTER. Whether an aircraft is a helicopter comes from the game's own pilot type, so a modded helicopter shows as one even when its name isn't in my lists.

Colors follow the side. Yellow is you and the other human pilots on your team, blue is AI and structures on your side, red is the enemy. You can change all three. Neutral things use the game's neutral color.

Under DISPLAY > View > Icons can be set to Game. Then every unit is drawn with the icon the game's own map uses, taken from your running game the first time a unit of that type shows up, so modded units get theirs too. The legend is hidden while it is on. Buildings keep their own dot. An icon that has no transparency gets it from its brightness, and one that comes out as a solid square or empty is left out and that unit keeps our symbol.

Each unit has its name next to it in its side's color. If a unit is the target of a lock, its name appears in the lock label instead, so the two don't overlap.

Ejected pilots are hidden by default. You can show them in FILTER.

Airbases are a square in the airbase's color with the name beside it. A line through the square means no hangar is free. Landing runways get a dashed line that reaches 5 km past both ends, so you can line up on final. You can switch these lines off in FILTER or change their color. If you have waypoints, a dashed line runs through them.

---

## Missiles, locks and warnings

Your own missiles are a bright cyan needle with the weapon name next to them. No checkbox can hide your own missile. Missiles from teammates are a duller color and carry the pilot's name or callsign with the weapon. A missile flown by AI shows just the weapon.

A missile that has a lock gets a pink line to its target and a ring around the target, with the target's name. Your own lines are solid and your teammates' are fainter.

When one shooter has several missiles out, the page doesn't label every one. It picks the one that will hit first and labels that. The others become small rings, and a "+N" on the label says how many more there are. Missiles flown by AI are never merged with each other.

If an air-to-air missile loses its lock, because of flares, notching or jamming, a ring shows where the target was and the line snaps and fades, with "LOCK LOST" next to it. A missile whose target was destroyed doesn't do this. If the same shooter locks that target again with another missile, the ring goes away.

FILTER can hide air-to-air locks, air-to-ground locks and missiles that have no lock. Your own missile always stays.

For incoming missiles the map uses the list your cockpit warning already built. You see a big needle that blinks red and yellow, and a box beside it with the word MISSILE, the speed in km/h and an ETA in seconds. The ETA is the straight-line distance divided by the missile's speed. Under 10 seconds the blink gets faster. If several missiles arrive together, every one is shown, and if their boxes would overlap, the boxes stack while the needles stay where the missiles are. The speed and the ETA are extra information from the map, since your cockpit only gives a tone and arrows, so they have a switch under DISPLAY, Missile warning.

---

## Who fired, jamming and other marks

All of these are under DISPLAY, each with its own switch, and the hover or long-press note of the switch says what it does.

**ATTACKER.** The aircraft that fired a missile at you gets a red ring and the tag ATTACKER, when it is on your map. The map only knows the shooter if your side tracks it. When the missile is gone, whether you evaded it, it missed or somebody shot it down, the ring stays for 30 more seconds. It dims like LAST POSITION does, follows the aircraft while your side still tracks it, and otherwise stays where it was last seen. Under the tag it says what fired and how long ago, for example `FS-20 Vortex · 12s ago`. Fair play hides it.

**Pulse.** Off by default. One soft glow along the screen edge when a missile starts coming at you (red) or when the jamming on you starts (yellow). It fades out in about a second and a half and never repeats, and there is no flashing. What was already there when the page opened doesn't pulse.

**Jamming.** The game's own map shows JAMMED on a unit whose radar is being jammed, with a line to the jammer. NOTacMap shows the same for the units your side can see: a JAMMED tag, and a line to the jammer when the jammer is on your map too. YOUR JAMMER ON shows while you are the one jamming (it goes out within a second of you stopping). YOU ARE JAMMED shows how strong it is when you are the target, and under it who jams you. RADAR DESTROYED shows on a unit whose radar was shot away. A destroyed radar can't wear the jamming off by itself, so without this tag it would stay JAMMED for good. All six are on by default. The radar tag is hidden by Fair play.

**Info card.** Hover a unit with the mouse, or tap it on a touch screen, and a small card shows its name, type, side, and the distance and bearing from you. Speed, altitude and heading are only shown for units on your own side. A tap or a click keeps the card open. A tap somewhere else or Esc closes it.

**LAST POSITION.** When you lose your aircraft (shot down, ejected, or you left it), your aircraft symbol stays where you were, with the tag LAST POSITION. It dims slowly while you wait, down to 30 percent over a minute. When you have an aircraft again it stays for 10 more seconds and fades, so you can see how far from the spot you are. A gap of under a second and a half in the data doesn't count as a loss.

**Nuclear blast radius.** The radius comes from the game's own numbers, the same formula its shockwave uses: 13 times the cube root of the yield (the 250 kt GPO-N reaches 8.18 km). Rings are amber for your side and red for the enemy. While the weapon you have selected is nuclear and has ammo, a dashed ring with BLAST and the distance sits around your marked target, or around the impact point when it is an unguided bomb. A nuclear missile in the air gets a dashed ring too, around its target, or around you when it is coming at you (NUKE or ENEMY NUKE). Where one really went off, a solid ring stays for a minute and dims. The map only draws that ring when the game itself made its nuclear shockwave there, so a missile that was shot down or didn't explode gets none. Fair play keeps only the rings of your own side. The ring around your own marked target or impact point stays, since it is your own weapon.

**Bomb impact point.** With an unguided bomb selected, a white mark shows where it would land if you let it go now, with IMPACT, the distance and the time the bomb needs to fall, and a dotted line from you. It is worked out the way the cockpit's own bombing display does it (the same drag, the same steps, the same line cast), so it sits on the cockpit's pipper. It isn't shown with the gear down, with no bombs left, or when the bomb would not land within the cockpit's own limit. Around the mark an amber ring shows how far the bomb's blast and fragments reach, taken from the game's damage code (20 times the cube root of the yield). That's the furthest it can hurt, not the area where everything is destroyed. For a nuclear bomb the ring is the nuclear blast radius. Guided and glide bombs don't get a mark. Fair play leaves it on, because it is your own cockpit's information.

---

## Marked targets

These are the targets you marked with your own weapon station. Each gets an amber reticle. The first one, the one that fires first, is bolder and says `TGT:` before its name.

If the tracker loses a marked target, the reticle turns into a dashed spinning marker at the last place it was seen, with `LOST:`, the name and how old that is. It fades over about 20 seconds. That's where the game last showed it, not where it is now. When contact comes back, the marker snaps back with a short spin and flash. If you unmark the target, the marker goes.

---

## Weapon range

The weapon you have selected gets a dashed circle around you showing its range. Guns don't get one.

Once you mark a target, the circle turns into a cone along your heading. The width of the cone is the weapon's own limit for how far off the nose it can go, so different weapons draw different cones. The range is worked out by the game's own missile range calculation, using your speed and altitude and the target's, and it's refreshed about once a second, the same as the cockpit HUD. The distance is written just past the tip of the cone.

The static circle is gray-blue and the cone is orange. Both colors can be changed, and FILTER can switch the whole thing off.

When a marked target is inside the cone, the map tells you in a few ways. Each has its own switch under DISPLAY, Marked target in range, and all are on by default. The cone is tinted. The brackets around the target close in. The range number lights up. One slow fade plays once when it enters, the only animation here, and it never repeats. Arrows show which way to turn when the target is in range but off to the side. A line shows how far is left until it is in range. A small chip counts how many of your marked targets are in range.

---

## Trails

Moving units leave a trail that fades out, 30 seconds long for units and 10 for missiles and bombs. Each trail is drawn in three steps, with the oldest part faintest. A parked unit never grows one.

FILTER has a folded section for trails with five switches: friendly aircraft, enemy aircraft, friendly missiles and bombs, enemy missiles and bombs, and ground units and ships.

---

## FILTER

Presets, at the top: All, Combat (all my weapons), Combat (selected weapon), Minimal and My choice. Combat reads the weapons on your own aircraft and ticks only what they can hit, and it follows your loadout, so a weapon you run out of drops out. The first Combat uses everything you carry, the second only the weapon you have selected. Minimal shows planes, helicopters, weapon range and missile trails. My choice goes back to the boxes the way you set them.

Unit types: Plane, Helicopter, Tank, APC, Radar, AA / SAM, Ship, Vehicle (other), Building and Pilot (ejected). All are on except Pilot.

Weapons: Air-to-air, Air-to-ground and Unlocked / other. All on.

Overlays: Runway approach guides, Weapon range, Grid, Distance rings and Show scale. All on except the rings.

Trails, in a fold: the five switches above. All on.

Everything you tick is saved.

---

## DISPLAY

The panel is folded into sections so it stays short: View, Missile warning, Marked target in range, Jamming, Marks and info, Page and names and Colors. Fair play stays at the top. A fold remembers whether you opened it. Many switches have a short note: hold the mouse over the label, or touch and hold it on a phone, and the note shows. The `?` at the end of a label marks those.

**View.** Map up (North or Your heading), Icons (Ours or Game), Units (Auto follows the unit setting in the game, or Meters or Feet), Which rings, Text size, the dark outline and Red HUD.

**Text.** Text size is Small, Normal, Large or X-Large, and Large is the default. It scales the text on the map and on the page. The dark outline behind text is on by default, and it keeps labels readable over bright terrain. "Show pilot names" is on by default and puts the pilot's name on teammates' missiles. "Use real names instead of callsigns" is also on by default, so teammates' real Steam names are shown. If you stream, turn it off: each teammate then gets a callsign from a fixed list for the session, and keeps it for as long as the page stays open.

**Map.** "Redraws per second when idle" is 5, 8, 12, 20 or 30 frames a second and 8 is the default. It goes up by itself while you pan, zoom or something is animating. "Sharp text and lines" draws at the screen's real pixel density. A phone gets up to three times, and the game PC is capped at two so it keeps its power for the game. It costs a bit more CPU. "Red HUD (night vision)" turns the green HUD text and the grid red. The page is dark with or without it.

**Page.** "Close this tab when the game closes" is on by default. "Pause the feed while this tab is hidden" is on too, and I explain it further down. "Keep the screen on" only does something on a phone that uses the secure link, see Phone and tablet.

**Colors.** Twelve of them: your team (human pilots), AI and structures, enemy, water, your missile, teammate missile, lock, marked target, runway guide, weapon range (static), weapon range (armed) and HUD text. Each one has a swatch that opens a honeycomb picker with a row of grays, boxes for RGB and hex, and a reset arrow. HUD text follows the game's own live HUD theme until you pick a color yourself, and reset puts it back to following the game.

At the bottom of the panel there's a link to report a bug.

---

## Legend, help and the status line

The legend sits in the top left and lists every symbol. (It is hidden while Icons is set to Game.) Tap it to open or close it, and it remembers what you chose. Next to it is a "? help" button with a short guide: quick start, how to read the map, what FILTER and DISPLAY do, the phone steps and what to check when something is wrong.

The status line says what's going on: `connecting...`, `LIVE` (with the number of friendly weapons in flight when there are any), `disconnected, retrying...`, `asleep (tab hidden)`, `reconnecting...`, `game closed, you can close this tab` or `showing the phone link`.

The colors of the HUD come from the game's live theme. Before a mission has loaded, the page uses a fallback palette and tells you so. It switches over on its own once the game's theme is there.

---

## Phone and tablet

This is off by default and works on your home network only.

When the page opens on the game PC it asks where you want to see the map, on this PC or on another device, with a "Remember my choice" box that is ticked by default, so you are only asked the first time. If you pick another device, the PC tab shows only a QR code and a link. It draws no map and keeps no connection open, so it costs no frame rate.

The first time you turn on access from other devices, a prompt says plainly what that opens up. Anyone who has the secret link can see where you and everyone else are, the connection is plain HTTP so it belongs at home and not on public wifi, and Windows may ask about the firewall.

The DEVICES button, which only the PC has, holds the switch, the QR code, New link, the QR screen and the setting for what the page does when it opens (ask me, show the map here, or show the phone link).

The QR code and the link stay hidden until you press "Show QR code and link". They hide again after two minutes, when you close the panel, or when you open another panel. That's on purpose. It keeps the code away from people next to you, and a code on screen can be hard for some people to look at.

New link makes a fresh secret and cuts off every device that had the old one. Deleting `LanToken` in the config and restarting does the same thing. Switching access off and on again keeps the same link. The QR screen is a big full-screen code and link, for a device that isn't in front of you.

You open the link, or scan the code, once on the phone. The phone keeps a cookie, so after that the page works without the secret in the address.

Only devices on a private network address are let in (192.168.x.x, 10.x.x.x, 172.16 to 31.x.x and 169.254.x.x), and only with the secret. Other websites can't read the map or change your settings. Each device gets three live streams, and the network can have 32 requests going at once, so one device can't take up the whole server.

If the PC changes network, access moves to the new address and the link on the map page updates. With no network at all, access waits until there is one.

Saving settings only works from the PC. A phone keeps its own display settings in its browser. The page is made for touch too: bigger buttons, panels that fit the screen, and a layout for a phone held sideways, with the buttons in a row and the color picker showing the numbers next to the honeycomb.

A web page may only keep a screen awake over HTTPS, and the normal link is plain HTTP on your home network. So on the normal link, set a longer screen timeout on the phone. On Android you can also turn on "Stay awake" in the developer options, which keeps the screen on while it charges. On an iPhone, set Auto-Lock to Never. The secure link below is the other way.

**Secure link (HTTPS, experimental).** In DEVICES, "Secure link" starts a second, encrypted address on port 8124, so it is a second port that Windows may ask about. The page on that link can keep the phone's screen on while the map is open (DISPLAY > Page and names > Keep the screen on, on by default). The plugin makes its own certificate the first time, in the game, and names only the address of this PC in it. It is self-signed, so the phone's browser warns that it cannot check the certificate. The link is still encrypted, but the phone can't tell who it is talking to, which is fine at home as long as you only tap through on a link you got from your own PC. To be sure, compare the SHA-256 fingerprint that DEVICES shows with the one in the certificate details on the phone. You will probably see the warning again later. The link still needs the secret, the same as the normal one, and the secure port is closed again when you switch access off or make a new link. The certificate and its private key are in `BepInEx/cache/https-cert.pfx` on your PC and nowhere else. It is renewed by itself when the address changes or has under 30 days left. The file stays where it is if you go back to an older version, and you can delete it. The secure link is new: it has had no outside security review yet, and I have not run it for hours or with a phone that goes to sleep. It is off by default, and I have only tried it with Chrome on Android so far, not with Samsung Internet or Safari. With `HttpsOnly` set to true in the config, other devices can only use the secure link and the plain link refuses them (this PC is not affected).

Don't forward the port. Anything forwarded to the PC (router port forwarding, WSL2, `netsh portproxy` or a reverse proxy) looks like it comes from the PC itself, and that never needs the secret.

---

## Settings and the config file

What you change on screen (FILTER, DISPLAY, DEVICES) goes into `settings.json` next to the plugin, and a phone keeps its own copy. If a later version changes a default, the old file is brought forward.

A few things live in `BepInEx/config/com.bigloude.notacmap.cfg` instead (or press F1 if you have the Configuration Manager). Every entry has a plain description.

| Entry | Default | What it does |
|---|---|---|
| Server > Port | 8123 | The port of the map page. Takes effect the next time the game starts. |
| Server > UpdateInterval | 0.2 | Seconds between updates, so 5 a second. |
| Server > AutoOpenBrowser | true | Opens the page when the game starts. |
| Server > AllowLan | false | Access from other devices. The DEVICES switch keeps it in step. |
| Server > LanAddress | empty | The network address to use. Found by itself when empty. |
| Server > LanToken | empty | The secret in the link. Made the first time you turn on access from other devices. If you write your own, use letters, digits, - and _, at least 16 characters. |
| Server > HttpsEnabled | false | The secure link. The DEVICES switch keeps it in step. |
| Server > HttpsPort | 8124 | The port of the secure link. |
| Server > HttpsOnly | false | With the secure link on, other devices can only use that one. |

---

## How light it is

The plugin builds one snapshot five times a second on the game's main thread and turns it into text on another thread, straight into a reused buffer so it makes little garbage for the game to clean up. If building takes more than 8 ms, a line appears in the log, at most once every 15 seconds. Flags that aren't set are left out, which keeps the data small on a busy server. The map icons of the Game setting are cut out of the game's textures in small steps, and not at all on a frame that is already slow.

When no page is connected, the plugin builds nothing. A hidden tab hangs up after ten seconds and reconnects when you look at it again. The page redraws slowly while nothing moves and speeds up while you pan, zoom or something animates. If the page is still reading the last update when the next one is ready, the next one is skipped instead of piling up. Every connected page is written to on its own, so a phone that stops answering can't hold up the PC. If it stays stuck for 5 seconds it is dropped, and the page reconnects by itself.

---

## Things to know

Grid references follow the offsets the game gives each map (80,000 meters each way on the standard maps, 120,000 across on the wide naval map). I checked them against the cockpit on two maps. If one map ever disagrees with your cockpit, please tell me which.

Unit types come from names plus the base type. An unknown modded aircraft shows as a plane. A modded helicopter shows as a helicopter when the game's own pilot type says so.

Marked targets are only yours. Sharing them would need two copies of the mod to talk to each other directly, and that's on the README's list of possible add-ons.

The normal phone link isn't encrypted. It's plain HTTP on your home network. The secure link is encrypted, but its certificate is the plugin's own, which is why the browser warns.

If Windows won't let the game listen on the network, the page and the log both say so, and the map keeps working on the PC.

Enemy missiles that your cockpit warning hasn't picked up yet are still drawn as a missile needle, just without the speed and ETA box. No unit filter hides them, the same as the incoming ones. Fair play does hide them. The speed and the ETA on the box are worked out by the mod from the missile's position, the cockpit itself only gives a tone and arrows.

The blast radius of a nuclear weapon and the reach of an ordinary bomb come from the game's own damage code, the impact point from the cockpit's own bombing display. The map shows what the game uses, I haven't measured real damage. The graphics card of one test PC hung in heavy explosion scenes. That was the driver and not this mod, and a newer driver is the first thing to try if it happens to you.
