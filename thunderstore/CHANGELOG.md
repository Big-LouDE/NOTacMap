# Changelog

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
