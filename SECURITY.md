# Security

NOTacMap serves the map from a small web server inside the game. By default only the PC that runs the game can reach it. If you turn on access from other devices, devices on your home network need a secret link, and the connection is plain HTTP, so use it at home and not on public wifi. The "Phone or tablet" section of the README says more.

## Reporting a problem

If you find a way to reach the map or change its settings without the secret link, a way for a web page to read the map or to change the settings, or anything else that looks like a security hole, please tell me privately and not in a public issue.

Use "Report a vulnerability" under the Security tab of this repository. It sends the report to me only. Please include what you did, what happened, and the NOTacMap version (it is in `BepInEx/LogOutput.log`). Don't include your own phone link.

This is a hobby project run by one person, so there is no fixed answer time, but security reports come before everything else.

## Versions

Only the latest release gets fixes.

## What counts

- Counts: reaching the map, the settings or the link from a device or a web page that should not be able to, getting around the secret link, or making the plugin do something to the game or the PC that it should not.
- Does not count: someone who has your phone link seeing your map, because that is what the link is for (use "New link" if it got out), someone who already has access to your PC, and the fact that the connection is not encrypted, which is written down above.
