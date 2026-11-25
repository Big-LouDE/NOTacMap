using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using NuclearOption.Networking;
using UnityEngine;

namespace NOTacMap
{
	// A one-time look at every radar and eye (TargetDetector) on every aircraft, vehicle and ship the game knows, written to the
	// BepInEx log once the game's encyclopedia is loaded. It answers how often each radar scans (checkInterval, and the
	// faster alertCheckInterval while it has contacts), how far it reaches and how wide its cone is, so the map's scan flash
	// and radar outline can follow the real numbers. Reads the prefabs only, changes nothing.
	internal static class RadarCheck
	{
		private static bool done;
		private static int tries;

		private static readonly FieldInfo CheckInterval = typeof(TargetDetector).GetField("checkInterval", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo AlertInterval = typeof(TargetDetector).GetField("alertCheckInterval", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo VisualRange = typeof(TargetDetector).GetField("visualRange", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo RadarCone = typeof(Radar).GetField("radarCone", BindingFlags.Instance | BindingFlags.NonPublic);

		// Called from Update. Tries now and then until the encyclopedia is there, then writes the list once.
		internal static void TryOnce()
		{
			if (done || ++tries % 60 != 0)
			{
				return;
			}
			if (tries > 60 * 600)
			{
				done = true; // about ten minutes of frames without an encyclopedia: give up quietly
				return;
			}
			Encyclopedia e;
			try
			{
				e = Encyclopedia.i;
			}
			catch (Exception)
			{
				return;
			}
			if (e == null || e.aircraft == null || e.aircraft.Count == 0)
			{
				return;
			}
			done = true;
			var lines = new List<string>();
			try
			{
				Add(lines, "aircraft", e.aircraft);
				Add(lines, "vehicle", e.vehicles);
				Add(lines, "ship", e.ships);
			}
			catch (Exception ex)
			{
				lines.Add("stopped early: " + ex.GetType().Name + ": " + ex.Message);
			}
			Plugin.Log.LogInfo("NOTacMap radar check, " + lines.Count + " sensors (scan = seconds between scans, contacts = seconds between scans while it has contacts):\n" + string.Join("\n", lines));
		}

		private static void Add<T>(List<string> lines, string kind, List<T> defs) where T : UnitDefinition
		{
			if (defs == null)
			{
				return;
			}
			foreach (T def in defs)
			{
				if (def == null || def.unitPrefab == null)
				{
					continue;
				}
				var c = CultureInfo.InvariantCulture;
				// how big it looks on radar (the game's radarSize, RCS before anything is hung on it), and its size
				lines.Add($"  {kind} | {def.unitName} | size on radar {def.radarSize.ToString("0.###", c)}");
				foreach (TargetDetector d in def.unitPrefab.GetComponentsInChildren<TargetDetector>(true))
				{
					string scan = Read(CheckInterval, d), contacts = Read(AlertInterval, d), eyes = Read(VisualRange, d);
					if (d is Radar radar)
					{
						string cone = Read(RadarCone, radar);
						RadarParams p = radar.RadarParameters;
						string range = (p.maxRange / 1000f).ToString("0.#", c) + " km";
						lines.Add($"  {kind} | {def.unitName} | radar {d.name} | range {range} | cone {(cone == "0" ? "all round" : cone + " deg half-angle")} | scan {scan} | contacts {contacts} | eyes {eyes} m"
							+ $" | minSignal {p.minSignal.ToString("0.###", c)} | maxSignal {p.maxSignal.ToString("0.###", c)} | clutterFactor {p.clutterFactor.ToString("0.###", c)} | dopplerFactor {p.dopplerFactor.ToString("0.#####", c)}");
					}
					else
					{
						lines.Add($"  {kind} | {def.unitName} | eyes {d.name} | scan {scan} | contacts {contacts} | eyes {eyes} m");
					}
				}
			}
		}

		// While you fly: every time one of YOUR OWN sensors finds a unit for the first time in this life, one log line with what
		// it was, how far, both altitudes, its size on radar and what the clean formula says (signal without clutter, Doppler or
		// jamming, and the reach that gives). Also when your radar is switched on or off, and the time of the first few scans.
		// It reads the game's own events on your aircraft and changes nothing. Called from Update.
		private static Aircraft watched;
		private static readonly List<TargetDetector> watchedDetectors = new List<TargetDetector>();
		private static readonly HashSet<PersistentID> seenThisLife = new HashSet<PersistentID>();
		private static bool radarWasOn;
		private static int linesThisLife, scansLogged;
		private static float lastScanTime;

		internal static void Watch()
		{
			try
			{
				Aircraft now = null;
				if (GameManager.GetLocalPlayer<Player>(out var player) && player.Aircraft != null && !player.Aircraft.disabled)
				{
					now = player.Aircraft;
				}
				if (now != watched)
				{
					Unhook();
					watched = now;
					if (now != null)
					{
						Hook(now);
					}
				}
				if (watched != null && watched.radar is Radar r && r.activated != radarWasOn)
				{
					radarWasOn = r.activated;
					Plugin.Log.LogInfo("NOTacMap radar watch: your radar is " + (radarWasOn ? "ON" : "OFF") + " at " + Time.timeSinceLevelLoad.ToString("0.0", CultureInfo.InvariantCulture) + " s");
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("NOTacMap radar watch stopped: " + ex.GetType().Name + ": " + ex.Message);
				Unhook();
				watched = null;
			}
		}

		private static void Hook(Aircraft aircraft)
		{
			seenThisLife.Clear();
			linesThisLife = 0;
			scansLogged = 0;
			radarWasOn = aircraft.radar is Radar r0 && r0.activated;
			string name = aircraft.definition != null ? aircraft.definition.unitName : aircraft.name;
			Plugin.Log.LogInfo("NOTacMap radar watch: now flying " + name + ", radar " + (aircraft.radar is Radar ? (radarWasOn ? "ON" : "OFF") : "none") + ", size on radar " + aircraft.RCS.ToString("0.###", CultureInfo.InvariantCulture));
			foreach (TargetDetector d in aircraft.GetComponentsInChildren<TargetDetector>(true))
			{
				TargetDetector det = d;
				det.onDetectTarget += OnDetect(det);
				if (det is Radar)
				{
					det.onScan += OnScan;
				}
				watchedDetectors.Add(det);
			}
		}

		private static readonly Dictionary<TargetDetector, Action<Unit>> handlers = new Dictionary<TargetDetector, Action<Unit>>();

		private static Action<Unit> OnDetect(TargetDetector det)
		{
			Action<Unit> h = target => Found(det, target);
			handlers[det] = h;
			return h;
		}

		private static void Unhook()
		{
			foreach (TargetDetector d in watchedDetectors)
			{
				if (d == null)
				{
					continue;
				}
				if (handlers.TryGetValue(d, out var h))
				{
					d.onDetectTarget -= h;
				}
				d.onScan -= OnScan;
			}
			watchedDetectors.Clear();
			handlers.Clear();
		}

		private static void OnScan()
		{
			if (scansLogged >= 4)
			{
				return;
			}
			scansLogged++;
			float t = Time.timeSinceLevelLoad;
			Plugin.Log.LogInfo("NOTacMap radar watch: radar scan at " + t.ToString("0.00", CultureInfo.InvariantCulture) + " s" + (scansLogged > 1 ? " (" + (t - lastScanTime).ToString("0.00", CultureInfo.InvariantCulture) + " s after the last)" : ""));
			lastScanTime = t;
		}

		private static void Found(TargetDetector det, Unit target)
		{
			if (target == null || watched == null || linesThisLife >= 200 || !seenThisLife.Add(target.persistentID))
			{
				return;
			}
			linesThisLife++;
			var c = CultureInfo.InvariantCulture;
			GlobalPosition me = det.GetScanPoint().GlobalPosition(), it = target.GlobalPosition();
			float dist = FastMath.Distance(me, it);
			string name = target.definition != null ? target.definition.unitName : target.name;
			string line = "NOTacMap radar watch: " + (det is Radar ? "RADAR" : "EYES") + " found " + name + " (" + FactionName(target) + ") at " + (dist / 1000f).ToString("0.0", c) + " km"
				+ ", you at " + me.y.ToString("0", c) + " m, it at " + it.y.ToString("0", c) + " m, its size on radar " + target.RCS.ToString("0.###", c);
			if (det is Radar radar)
			{
				RadarParams p = radar.RadarParameters;
				float clean = p.maxRange / Mathf.Max(dist, 1f) * Mathf.Pow(Mathf.Max(target.RCS, 0f), 0.25f);
				float reach = p.minSignal > 0f ? p.maxRange * Mathf.Pow(Mathf.Max(target.RCS, 0f), 0.25f) / p.minSignal : 0f;
				line += ", clean signal " + clean.ToString("0.###", c) + " (needs " + p.minSignal.ToString("0.###", c) + "), clean reach " + (reach / 1000f).ToString("0.0", c) + " km";
			}
			Plugin.Log.LogInfo(line);
		}

		private static string FactionName(Unit u)
		{
			try
			{
				return u.NetworkHQ != null && u.NetworkHQ.faction != null ? u.NetworkHQ.faction.factionName : "no side";
			}
			catch (Exception)
			{
				return "?";
			}
		}

		private static string Read(FieldInfo f, object o)
		{
			if (f == null)
			{
				return "?";
			}
			object v = f.GetValue(o);
			return v is float x ? x.ToString("0.##", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture);
		}
	}
}
