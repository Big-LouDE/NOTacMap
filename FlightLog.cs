using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using NuclearOption.Networking;
using UnityEngine;

namespace NOTacMap
{
	// The flight log for beta testers: one plain text file per flight in BepInEx/NOTacMap-logs, the newest 20 kept. It stays on
	// this PC; a tester sends a file only by attaching it to a GitHub issue or the discussion themselves. Every part can be
	// switched off in F1 > NOTacMap > Flight log, where a box says what is written, how and where.
	// Never written: player names (only unit types), chat, server names, network addresses, the phone link's secret, pictures.
	internal static class FlightLog
	{
		private const int KeepFiles = 20;
		internal static string Folder => Path.Combine(Paths.BepInExRootPath, "NOTacMap-logs");

		private static ConfigEntry<bool> on, errors, performance, screens, controls, radar, page, settingsAtStart;
		private static ConfigFile config;
		private static bool showInfo;      // the info box in F1 is open
		private static bool justSwitchedOn;

		private static readonly object writeLock = new object();
		private static StreamWriter file;
		private static string fileName;
		private static float flightStart;
		private static Aircraft flying;
		private static readonly List<string> beforeFlight = new List<string>(); // things that happened between flights
		private static readonly List<string> earlyLines = new List<string>(); // this flight, before its file is open
		private static bool pendingStart;

		// numbers for the line every minute
		private static float nextMinute;
		private static double buildMsSum; private static float buildMsMax; private static int builds; private static int unitsLast;
		private static long bytesSum; private static int bytesCount;
		private static double frameSum; private static float frameMax; private static int frames;
		private static int[] pressesAtMinute = new int[3];
		private static int warnLock, warnFound, warnSwept;
		private static int pageErrors;
		private static int missilesRadar, missilesEyes;

		internal static void Init(ConfigFile cfg)
		{
			config = cfg;
			var about = new ConfigurationManagerAttributes { Order = 100, HideDefaultButton = true, CustomDrawer = _ => DrawInfo() };
			config.Bind("Flight log", "About", "", new ConfigDescription("What the flight log records, how and where.", null, about));
			on = config.Bind("Flight log", "On", true, new ConfigDescription("Write a flight log for beta testing (one text file per flight in BepInEx/NOTacMap-logs, on this PC only). Open the box above to see exactly what is in it.", null, new ConfigurationManagerAttributes { Order = 90, DispName = "Flight log on" }));
			errors = Part("Errors", "Problems the mod runs into, with the time.", 80);
			performance = Part("Performance", "Once a minute: how long the map update takes, how many units and how much data, and the game's frame time.", 79);
			screens = Part("Screens", "When a screen opens the map: this PC or another device, secure link or not, and how many are connected. Never an address.", 78);
			controls = Part("Keys and controllers", "Which controllers the game sees, what you set the map keys to, and how often they were pressed.", 77);
			radar = Part("Radar", "Your plane's type, what your radar and eyes find (unit type, side, distance, both altitudes, size on radar), radar warnings on you, radar on/off, jamming.", 76);
			page = Part("Map page errors", "Errors inside the map page, also from a phone, so problems on another device can be found.", 75);
			settingsAtStart = Part("Settings at the start", "Your map settings (FILTER, DISPLAY) when a flight starts, so what you saw can be repeated.", 74);
			on.SettingChanged += (_, __) =>
			{
				if (on.Value) { showInfo = true; justSwitchedOn = true; }
				else EndFlight("The flight log was switched off.");
			};
		}

		private static ConfigEntry<bool> Part(string name, string what, int order)
		{
			return config.Bind("Flight log", name, true, new ConfigDescription(what, null, new ConfigurationManagerAttributes { Order = order }));
		}

		// ---- F1: the box with what, how and where
		private static GUIStyle wrap;
		private static void DrawInfo()
		{
			if (wrap == null) wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
			GUILayout.BeginVertical();
			GUILayout.BeginHorizontal();
			GUILayout.Label(on.Value ? "The flight log is ON." : "The flight log is off.", GUILayout.ExpandWidth(true));
			if (GUILayout.Button(showInfo ? "Hide what is recorded" : "What is recorded, how and where", GUILayout.ExpandWidth(false))) { showInfo = !showInfo; justSwitchedOn = false; }
			GUILayout.EndHorizontal();
			if (showInfo)
			{
				if (justSwitchedOn) GUILayout.Label("You just switched it on. This is what it will write:", wrap);
				GUILayout.Label(InfoText(), wrap);
				GUILayout.BeginHorizontal();
				if (GUILayout.Button("Open the logs folder", GUILayout.ExpandWidth(false)))
				{
					try { Directory.CreateDirectory(Folder); Application.OpenURL("file:///" + Folder.Replace('\\', '/')); }
					catch (Exception ex) { Plugin.Log.LogWarning("NOTacMap: could not open the logs folder: " + ex.Message); }
				}
				GUILayout.EndHorizontal();
			}
			GUILayout.EndVertical();
		}

		internal static string InfoText()
		{
			var sb = new StringBuilder();
			sb.AppendLine("WHERE: " + Folder);
			sb.AppendLine("One plain text file per flight, named by date, time and map. Only the newest " + KeepFiles + " are kept, older ones are deleted. You can open any file and read it.");
			sb.AppendLine();
			sb.AppendLine("WHAT (each part can be switched off below):");
			sb.AppendLine("- Errors: problems the mod runs into.");
			sb.AppendLine("- Performance, once a minute: map update time, number of units, data size, the game's frame time.");
			sb.AppendLine("- Screens: this PC or another device, secure link or not, how many. No addresses.");
			sb.AppendLine("- Keys and controllers: controllers the game sees, what the map keys are set to, how often pressed.");
			sb.AppendLine("- Radar: your plane's type, what your radar and eyes find (type, side, distance, altitudes, size on radar), radar warnings on you, radar on/off, jamming.");
			sb.AppendLine("- Map page errors, also from a phone.");
			sb.AppendLine("- Your map settings when a flight starts.");
			sb.AppendLine("Every file ends with a line saying the flight ended normally. If that line is missing, the game stopped unexpectedly.");
			sb.AppendLine();
			sb.AppendLine("NEVER: names of players (units are written by type only), chat, server names, network addresses, the phone link's secret, pictures.");
			sb.AppendLine();
			sb.AppendLine("HOW IT GETS TO US: it doesn't, by itself. Nothing is sent anywhere. If you want to help, open the folder and drag the file of that flight into a GitHub issue or the NOTacMap discussion, and say what you saw.");
			return sb.ToString();
		}

		// ---- the file
		private static bool Want(ConfigEntry<bool> part) => on != null && on.Value && part.Value;

		private static void StartFlight(Aircraft aircraft)
		{
			if (!on.Value) return;
			try
			{
				Directory.CreateDirectory(Folder);
				string map = (MapImageCapture.MapName ?? "map").Replace("(Clone)", "").Trim();
				foreach (char c in Path.GetInvalidFileNameChars()) map = map.Replace(c, '_');
				fileName = Path.Combine(Folder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) + "_" + map + ".txt");
				lock (writeLock)
				{
					file = new StreamWriter(fileName, false, new UTF8Encoding(false)) { AutoFlush = true };
				}
				nextMinute = flightStart + 60f;
				string plane = aircraft.definition != null ? aircraft.definition.unitName : "an aircraft";
				Write("NOTacMap flight log. NOTacMap " + Plugin.PluginVersion + (Plugin.BuildLabel.Length > 0 ? " " + Plugin.BuildLabel : "") + ", Nuclear Option " + Plugin.GameVersion + ", map " + map + ".", false);
				Write("Started " + DateTime.Now.ToString("d MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture) + ". Times below are minutes:seconds into the flight. Player names, chat, server names and addresses are never written.", false);
				Write("You are flying a " + plane + ".", true);
				lock (writeLock)
				{
					if (beforeFlight.Count > 0)
					{
						file.WriteLine("Before this flight:");
						foreach (string s in beforeFlight) file.WriteLine("  " + s);
					}
					beforeFlight.Clear();
					foreach (string s in earlyLines) file.WriteLine(s);
					earlyLines.Clear();
				}
				if (Want(settingsAtStart)) WriteSettings();
				Prune();
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("NOTacMap: the flight log could not be started: " + ex.Message);
				file = null;
			}
		}

		private static void EndFlight(string why)
		{
			lock (writeLock)
			{
				if (file == null) return;
				try
				{
					file.WriteLine(Stamp() + "The flight ended normally after " + Minutes(Time.unscaledTime - flightStart) + ". " + why);
					file.Dispose();
				}
				catch (Exception)
				{
					// the disk went away; nothing more to write
				}
				file = null;
			}
		}

		private static void Prune()
		{
			try
			{
				var all = new List<FileInfo>(new DirectoryInfo(Folder).GetFiles("*.txt"));
				all.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
				for (int i = KeepFiles; i < all.Count; i++) all[i].Delete();
			}
			catch (Exception)
			{
				// an old file in use is left for next time
			}
		}

		private static string Stamp()
		{
			float t = Math.Max(0f, Time.unscaledTime - flightStart);
			return ((int)t / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + ((int)t % 60).ToString("00", CultureInfo.InvariantCulture) + "  ";
		}

		private static string Minutes(float s) => s < 90 ? ((int)s) + " seconds" : (s / 60f).ToString("0.0", CultureInfo.InvariantCulture) + " minutes";

		// From any thread. Between flights the line waits and goes at the top of the next file.
		private static void Write(string line, bool stamp = true)
		{
			lock (writeLock)
			{
				if (file == null)
				{
					if (on == null || !on.Value) return;
					if (pendingStart) { if (earlyLines.Count < 200) earlyLines.Add((stamp ? StampSafe() : "") + line); }
					else if (beforeFlight.Count < 100) beforeFlight.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
					return;
				}
				try { file.WriteLine((stamp ? StampSafe() : "") + line); }
				catch (Exception) { file = null; }
			}
		}

		// Time.unscaledTime may only be read on the main thread; other threads use the wall clock.
		private static readonly int mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
		private static DateTime flightStartWall = DateTime.Now;
		private static string StampSafe()
		{
			if (System.Threading.Thread.CurrentThread.ManagedThreadId == mainThread) return Stamp();
			double t = Math.Max(0, (DateTime.Now - flightStartWall).TotalSeconds);
			return ((int)t / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + ((int)t % 60).ToString("00", CultureInfo.InvariantCulture) + "  ";
		}

		private static void WriteSettings()
		{
			try
			{
				string path = Path.Combine(Path.GetDirectoryName(typeof(FlightLog).Assembly.Location), "settings.json");
				string json = File.Exists(path) ? File.ReadAllText(path) : "";
				Write("Your map settings at the start (settings.json): " + (json.Length == 0 ? "none saved yet" : json.Replace("\r", "").Replace("\n", " ")));
			}
			catch (Exception ex)
			{
				Write("Your map settings could not be read: " + ex.Message);
			}
		}

		// ---- called by the rest of the mod

		// Main thread, every frame.
		internal static void Update(Aircraft now, int clients, KeyPresses presses)
		{
			if (on == null) return;
			if (now != flying)
			{
				if (flying != null) EndFlight(now == null ? "You left the aircraft, or it was lost." : "You changed aircraft.");
				flying = now;
				pendingStart = false;
				earlyLines.Clear();
				if (now != null && on.Value)
				{
					// the file waits until the mod has read this mission's map (its name goes into the file), at most 20 s
					flightStartWall = DateTime.Now;
					flightStart = Time.unscaledTime;
					pendingStart = true;
				}
			}
			if (pendingStart && flying != null && (MapImageCapture.Current || Time.unscaledTime - flightStart > 20f))
			{
				pendingStart = false;
				StartFlight(flying);
			}
			float dt = Time.unscaledDeltaTime * 1000f;
			frameSum += dt; frames++; if (dt > frameMax) frameMax = dt;
			if (file != null && Time.unscaledTime >= nextMinute)
			{
				nextMinute = Time.unscaledTime + 60f;
				MinuteLine(clients, presses);
			}
		}

		private static void MinuteLine(int clients, KeyPresses presses)
		{
			var c = CultureInfo.InvariantCulture;
			var parts = new List<string>();
			if (Want(performance))
			{
				if (builds > 0) parts.Add("the map update took " + (buildMsSum / builds).ToString("0.0", c) + " ms on average (longest " + buildMsMax.ToString("0.0", c) + " ms) for " + unitsLast + " units, about " + (bytesCount > 0 ? (bytesSum / bytesCount / 1024).ToString(c) : "?") + " KB per update");
				if (frames > 0) parts.Add("the game took " + (frameSum / frames).ToString("0.0", c) + " ms per frame on average (longest " + frameMax.ToString("0", c) + " ms, about " + (frameSum > 0 ? (1000.0 * frames / frameSum).ToString("0", c) : "?") + " fps)");
			}
			if (Want(screens)) parts.Add(clients + (clients == 1 ? " screen" : " screens") + " showing the map");
			if (Want(controls) && presses != null)
			{
				int a = presses.combat - pressesAtMinute[0], b = presses.zoomIn - pressesAtMinute[1], d = presses.zoomOut - pressesAtMinute[2];
				if (a + b + d > 0) parts.Add("map keys pressed: Combat or All " + a + ", zoom in " + b + ", zoom out " + d);
				pressesAtMinute = new[] { presses.combat, presses.zoomIn, presses.zoomOut };
			}
			if (Want(radar) && warnLock + warnFound + warnSwept > 0) parts.Add("radar warnings on you: locked " + warnLock + ", found you " + warnFound + ", swept over you " + warnSwept);
			if (Want(radar) && missilesRadar + missilesEyes > 0) parts.Add("missiles and rockets seen: " + missilesRadar + " by your radar, " + missilesEyes + " by your eyes");
			if (parts.Count > 0) Write("In the last minute: " + string.Join("; ", parts.ToArray()) + ".");
			buildMsSum = 0; buildMsMax = 0; builds = 0; bytesSum = 0; bytesCount = 0; frameSum = 0; frameMax = 0; frames = 0;
			warnLock = warnFound = warnSwept = 0;
			missilesRadar = missilesEyes = 0;
		}

		internal static void Build(float ms, int units)
		{
			buildMsSum += ms; builds++; unitsLast = units; if (ms > buildMsMax) buildMsMax = ms;
		}

		// Worker thread.
		internal static void Sent(int bytes)
		{
			System.Threading.Interlocked.Add(ref bytesSum, bytes);
			System.Threading.Interlocked.Increment(ref bytesCount);
		}

		internal static void Error(string what, Exception ex)
		{
			if (Want(errors)) Write("A problem while " + what + ": " + ex.GetType().Name + ": " + ex.Message);
		}

		internal static void Screen(bool otherDevice, bool secure, int deviceNumber)
		{
			if (Want(screens)) Write((otherDevice ? "Device " + deviceNumber + " (not this PC)" : "This PC") + " opened the map" + (secure ? " over the secure link" : "") + ".");
		}

		internal static void Controls(string line)
		{
			if (Want(controls)) Write(line);
		}

		internal static void PageError(string text, bool otherDevice)
		{
			if (!Want(page) || pageErrors >= 50) return;
			pageErrors++;
			Write("The map page " + (otherDevice ? "on another device" : "on this PC") + " had an error: " + text);
		}

		internal static void RadarLine(string line)
		{
			if (Want(radar)) Write(line);
		}

		// A missile or rocket one of your sensors saw for the first time: only counted, they come in volleys.
		internal static void MissileSeen(bool byRadar)
		{
			if (byRadar) missilesRadar++; else missilesEyes++;
		}

		internal static void Warning(string kind)
		{
			if (kind == "target") warnLock++; else if (kind == "detected") warnFound++; else warnSwept++;
		}

		// The game is closing.
		internal static void Close()
		{
			EndFlight("The game was closed.");
		}
	}
}
