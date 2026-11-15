# Changelog

## 1.3.0

Added
- Where an unguided bomb will land: with a bomb selected, a mark shows the impact point, worked out the way the cockpit's bombing display does it, and a ring around it shows how far the blast and the fragments reach
- Nuclear weapons show their blast radius, from the game's own numbers: around your marked target (or the bomb's impact point) while a nuclear weapon is selected, around nuclear missiles in the air, and where one went off. A missile that was shot down or did not explode gets no ring
- Jamming on the map, like on the game's own map: a JAMMED tag on units whose radar is jammed, a line to the jammer, "YOUR JAMMER ON" when you jam and "YOU ARE JAMMED" with who jams you. A RADAR DESTROYED tag shows on a unit whose radar was shot away. Each part has its own switch
- ATTACKER: the aircraft that fired a missile at you gets a red ring and the tag ATTACKER. The ring stays for 30 seconds after the missile is gone, dimming, and says what it was and how long ago
- Fair play, a switch at the top of DISPLAY: it hides what your cockpit does not show you (speed and ETA of incoming missiles, missiles your warning has not picked up, who fired at you, RADAR DESTROYED and the rings of enemy nuclear missiles). Your other switches stay as they are
- Info card: tap a unit, or hover it with a mouse, to see its name, type, side, and the distance and bearing from you. Your own side also shows altitude, heading and speed. An enemy only shows what the game's own tooltip shows
- LAST POSITION: when you lose your aircraft, its symbol stays where you were, dimming slowly, and stays 10 seconds after you have a new one
- Heading up: DISPLAY > Map up > Your heading turns the map so you always fly up the screen, with a small compass. A button under RECENTER switches between the two
- Game icons: DISPLAY > Icons > Game draws every unit with the icon of the game's own map, taken from your running game
- The sea goes on past the edge of the map and gets darker the further out you go. Inside the map it is placed where the game says the sea is, in a deep blue you can change under DISPLAY, Water
- Units in DISPLAY: Auto follows the unit setting in the game, or pick Meters or Feet (ft, knots, nautical miles). Idea from Hollow-Ruin
- Presets at the top of FILTER: All, Minimal, and two Combat views. Combat reads the weapons you carry and shows only what they can hit
- When a marked target is inside your weapon cone: the cone is tinted, the brackets around the target close in, the range number lights up and a slow fade plays once. Arrows show which way to turn when the target is in range but off to the side, a line shows how far is left until it is in range, and a small chip counts how many of your marked targets are in range. Each has its own switch
- Distance rings (5, 10, 20 and 40 km, or 2, 5, 10 and 20 nm) and a scale bar, both in FILTER. The rings are off by default
- A switch for the speed and the time to impact in the incoming missile box
- Pulse, off by default: one soft glow along the screen edge when a missile starts coming at you (red) or the jamming on you starts (yellow)
- A rotate button for phones and tablets, and a MENU button that holds fullscreen, rotate and devices
- An experimental secure link (HTTPS), off by default and marked EXPERIMENTAL in DEVICES: DEVICES > Secure link lets a phone keep its screen on. It opens a second port (8124) with a certificate the plugin makes itself, so the phone warns that it cannot check it. It has had no outside security review yet. The notes in DEVICES and in the README say what that means
- The screen after "On another device" has the Secure link switch, New link and "Turn off access from other devices" next to the QR code, so the link can be set up in one place. Turning access off there sets a remembered "On another device" back to "Ask me"
- The log and the Help window say so when your game is not the version NOTacMap was last checked with (now 0.34.6)

Still welcome: outside testing
- The secure link has had no outside security review. If you know TLS, a look at TlsFront.cs, TlsCertificate.cs and RequestGuard.cs is the most useful help I can get
- Edge cases I have not tried: Samsung Internet, Safari and Firefox on the secure link, a phone that goes to sleep and wakes up, a session of several hours, a modded helicopter, the game's own icons with modded units, and Linux
- Tell me in the Discussions tab or open an issue

Changed
- DISPLAY and FILTER are in folded sections now, and a switch with a ? has a note: hold the mouse over it, or touch and hold
- Zoom follows how far you scroll and, on a map you have moved, zooms toward the cursor. A pinch keeps you in the center while the map follows you
- When you die or eject, the map stays where you were instead of jumping to the middle
- Helicopter icons come from the game's own data, so mods with their own helicopters work without an update
- The grid reference (like Gh98) is in the bottom right corner now. It sat on top of the HELP tag, and the status text and buttons covered the grid numbers along the top
- The snapshot is written straight into a reused buffer, which is about a third of the allocation on a busy server
- The map picture and the unit icons are sent with no-store, so a changed map or icon is never shown stale
- The description in the README and on Thunderstore says the map is made first for HOTAS pilots and also works for anyone who wants more information on a second monitor, a tablet or a phone

Fixed
- An enemy missile that your cockpit warning has not picked up yet is drawn as a missile needle, not a plane arrow (also in 1.2.4)
- A browser tab that was hidden when the game closed now closes too (also in 1.2.4)
- Icon capture does nothing on a frame that is already slow, so it cannot add to a hitch

## 1.2.4

Fixed
- An enemy missile that your cockpit warning had not picked up yet was drawn as a plane arrow. It is a small missile needle now, like the other missiles. The feature list on GitHub already claimed this, which was wrong for 1.2.3
- A browser tab that was hidden when the game closed (the map, the first question or the phone link screen) stayed open. It closes now too (a hidden tab can take a minute or two to react), with the same setting as before ("Close this tab when the game closes")

## 1.2.3

Fixed
- If the port is already in use (the NOX mod uses 8111), the log now says so in one clear line, and the game carries on without the map. Before, you got a long error
- A phone that stops answering, out of wifi range for example, no longer freezes the map on the PC. Before, one stuck phone could stop every page from getting updates. Now each page is written to separately, and one that is stuck for 5 seconds is dropped. The page reconnects by itself
- After changing to a new mission, the weapon range on the map froze at its last value until the new mission had run as long as the old one. It updates again right away
- The grid reference on the map was wrong on the wide naval map (the cockpit said Hf120, the map said Hf80), because the grid offset was fixed. The mod now reads it from the game for every map
- A short hitch every 2 seconds is gone. The check for a new map cost about 5 ms each time, and now looks again only every 30 seconds once the map is loaded
- A map page that was left on the first question ("On this PC / On another device"), or on the phone link, never noticed that the game had closed and stayed open. It now closes itself there too, with the same rule as the map (the "Close this tab when the game closes" setting)
- A game update that breaks one internal call no longer fills the log. The problem is logged once
- Port and update interval in the config can't be set to nonsense values anymore
- Two open tabs saving settings at the same moment can't damage settings.json anymore
- A map name with a quote or a backslash no longer breaks the map picture
- The phone link no longer shows in the F1 config window, where it could appear on a stream
- With a custom HUD color picked, dark mode still turns the HUD red, and resetting the HUD color goes back to the game's own
- Typing in the color boxes saves once, a moment after you stop
- A contact you have lost keeps its last heading, the same as on the game's own map. The map no longer tells you which way it is turning
- The mod no longer sends the real speed of units it doesn't show a speed for

Changed
- "Remember my choice" on the first question is ticked by default, so you are asked once
- Clearer labels for the yellow color ("Your team (human pilots)"), dark mode ("Red HUD (night vision)") and the redraw rate ("Redraws per second when idle")

Added
- A detailed feature list in FEATURES.md
- A note that the map works in multiplayer, and that it's made with HOTAS pilots in mind
- Tips for keeping a phone screen on

## 1.2.2

Fixed
- The map page showed "Bad Request (Invalid host)" in browsers that reach `localhost` over IPv4, Firefox for one. The game now opens the page at `http://127.0.0.1:8123/`, which every browser can reach, and the README says to use that address. If you typed `localhost` yourself, use `http://127.0.0.1:8123/` instead. Reported by LokiRokanafult on GitHub (issue #1), thank you for the detailed report
- If a new link can't be saved to the config, the page says so and nothing changes

## 1.2.1

New
- A "? help" button next to the legend explains the map: quick start, the colors and symbols, FILTER and DISPLAY, the phone or tablet view, and what to check when something is wrong
- A "New link" button in DEVICES makes a new phone link and cuts off every device that has the old one, without restarting the game
- The phone settings have their own DEVICES button now, and DISPLAY is grouped into Text, Map, Page and Colors
- The QR code and the link stay hidden until you press "Show QR code and link", and hide again after 2 minutes

Better
- Every setting in the BepInEx config has a plain-language description
- If you use the access switch just as the game is closing, the page says the game is closing instead of blaming Windows
- The README is shorter

Security
- In current browsers, other websites can't use up the map's few connection slots any more: the live stream only answers the map page itself
- A device on your network can use at most 3 live streams and 32 requests at a time

## 1.2.0

New
- Open the map on a phone or tablet on your home network. Off by default: pick "On another device" when the map page opens, or use the switch in DISPLAY > Phone / tablet. The page works with touch (drag to pan, pinch to zoom, fullscreen button) and has a landscape layout
- The map page on the PC first asks where you want to see the map. "On another device" turns on access from other devices after a short prompt and shows a QR code and the link. It draws no map on the PC. "Remember my choice" skips the question and the prompt next time. The switch in DISPLAY turns it off again without restarting the game. It stays on after a restart until you do
- The link follows your PC if it changes network or address while the game is running
- Tap the legend to open or close it
- Colors are picked from a honeycomb palette, with RGB and hex boxes for exact values
- A dark outline behind the map's text so it stays readable over the terrain. Switch it off in DISPLAY
- Text and lines are drawn at the screen's real pixel density, so they are sharp on a phone. Switch it off in DISPLAY if it costs too much
- A "Send feedback or report a bug" link at the bottom of DISPLAY

Security
- LAN access only accepts devices on private network addresses, and they need the secret in the link. The phone remembers it with a cookie afterwards. Saving settings only works from the PC. Delete LanToken from the config and restart the game to cut off old links
- The connection is plain HTTP, so use it at home and not on public wifi

## 1.1.1

Better
- Big drop in how much the map costs your FPS. It redraws 8 times a second while nothing happens (changeable under DISPLAY) and goes faster while you pan, zoom or something animates
- Units off screen are no longer drawn, and standing structures no longer get trails
- Each update from the game is roughly 25 to 60% smaller, depending on the map
- Sleep and wake: a tab that has been hidden for 10 seconds stops asking for updates, and wakes up again when you come back. Can be turned off under DISPLAY
- The theme loads by itself once you're in a mission, no manual reload
- Your name label and the speed/altitude block no longer overlap

Security
- Other websites can't read the map or overwrite your saved settings anymore
- Settings saves are limited to 64 KB of valid JSON, and only 8 tabs can connect at once

## 1.1.0

New
- Weapon range ring around your aircraft. A circle that becomes a cone once you mark a target
- Unit trails, with a Trails folder in FILTER to pick which kinds
- LOST marker when you lose contact with a marked target, and a snap when you pick it up again
- A ring and a snapping line when a missile loses its lock on an aircraft (yours and teammates')
- Runway approach lines
- Editable map colors, grid on/off, dark mode and real names/callsigns, all in the panels. No more URL options
- The tab closes itself when the game closes. Double-click the map for fullscreen

Better
- With several missiles out, only the closest one gets the full lock label and the rest are small rings (+N). Labels no longer pile up
- Marked targets and locks use the game's own tracker, so they don't show where a target really is after you've lost it
- Teammate callsigns stay the same after a respawn
- Ejected pilots are hidden by default
- Human teammates are yellow, AI and structures blue
- Missile warnings stack instead of overlapping

## 1.0.0

First release.
