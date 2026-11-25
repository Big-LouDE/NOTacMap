using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using NuclearOption.Networking;
using UnityEngine;

namespace NOTacMap
{
	// Your own radar for the map: its cone, when it scans, what it finds, and the radar warnings the game gives you.
	// All of it is read from YOUR aircraft only, the same events the game's own HUD and map listen to:
	// - Radar.onScan (your radar's search runs on your PC, every 4 s on the planes in this game)
	// - TargetDetector.onDetectTarget on your radar and eyes
	// - Aircraft.onRadarWarning on your aircraft (the game sends it to every PC, but only your own aircraft's warnings are
	//   shown in your cockpit and on your map, DynamicMap.ShowRadarPing, so this listens to yours only)
	internal static class OwnRadar
	{
		private static readonly FieldInfo RadarCone = typeof(Radar).GetField("radarCone", BindingFlags.Instance | BindingFlags.NonPublic);

		private class Find
		{
			public int pings;        // scans of your radar that saw it
			public bool radar;       // seen by your radar at least once (else only by your eyes)
			public float lastSeen;   // Time.unscaledTime
		}

		private class Warning
		{
			public uint emitter;
			public float x, z;
			public string kind;      // "target" | "detected" | "passive", like the game's own map
			public float time;
		}

		private static Aircraft watched;
		internal static Aircraft Watched => watched;
		private static bool wasOn, wasJammed;
		private static readonly HashSet<uint> loggedFinds = new HashSet<uint>(), loggedEmitters = new HashSet<uint>();
		private static Radar radar;
		private static readonly List<TargetDetector> detectors = new List<TargetDetector>();
		private static readonly Dictionary<TargetDetector, Action<Unit>> handlers = new Dictionary<TargetDetector, Action<Unit>>();
		private static readonly Dictionary<uint, Find> finds = new Dictionary<uint, Find>();
		private static readonly List<Warning> warnings = new List<Warning>();
		private static int scans;

		// A find that no sensor of yours has seen for this long is dropped (a bit more than two radar scans).
		private const float ForgetAfter = 9f;
		// How long a warning stays in the data, the longest the game's map keeps one (a lock, 4 s).
		private const float WarningLife = 4f;

		// Main thread, every frame.
		internal static void Update()
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
					if (now != null) Hook(now);
				}
			}
			catch (Exception)
			{
				Unhook();
				watched = null;
			}
		}

		private static void Hook(Aircraft aircraft)
		{
			finds.Clear();
			warnings.Clear();
			loggedFinds.Clear();
			loggedEmitters.Clear();
			scans = 0;
			radar = aircraft.radar as Radar;
			wasOn = radar != null && radar.activated;
			wasJammed = false;
			if (radar == null) FlightLog.RadarLine("This plane has no radar, only eyes.");
			var all = new List<TargetDetector>(aircraft.GetComponentsInChildren<TargetDetector>(true));
			if (aircraft.radar != null && !all.Contains(aircraft.radar)) all.Add(aircraft.radar);
			foreach (TargetDetector d in all)
			{
				TargetDetector det = d;
				Action<Unit> h = target => Found(det, target);
				det.onDetectTarget += h;
				handlers[det] = h;
				if (det is Radar) det.onScan += OnScan;
				detectors.Add(det);
			}
			aircraft.onRadarWarning += OnWarning;
		}

		private static void Unhook()
		{
			foreach (TargetDetector d in detectors)
			{
				if (d == null) continue;
				if (handlers.TryGetValue(d, out var h)) d.onDetectTarget -= h;
				d.onScan -= OnScan;
			}
			detectors.Clear();
			handlers.Clear();
			if (watched != null) watched.onRadarWarning -= OnWarning;
			radar = null;
			finds.Clear();
			warnings.Clear();
		}

		private static void OnScan()
		{
			scans++;
		}

		private static void Found(TargetDetector det, Unit target)
		{
			if (target == null) return;
			uint id = target.persistentID.Id;
			if (!finds.TryGetValue(id, out Find f)) finds[id] = f = new Find();
			if (det is Radar)
			{
				f.pings++;
				f.radar = true;
			}
			f.lastSeen = Time.unscaledTime;
			Stats.Found(target, det is Radar);
			if (watched != null && loggedFinds.Count < 300 && loggedFinds.Add(id))
			{
				if (target is Missile) { FlightLog.MissileSeen(det is Radar); return; }
				if (target.NetworkHQ == null && target.RCS <= 0f) return; // props without a side (camo nets, pallets)
				var c = System.Globalization.CultureInfo.InvariantCulture;
				GlobalPosition me = det.GetScanPoint().GlobalPosition(), it = target.GlobalPosition();
				string type = target.definition != null ? target.definition.unitName : "a unit";
				string side = target.NetworkHQ == null ? "no side" : target.NetworkHQ == watched.NetworkHQ ? "your side" : "enemy";
				FlightLog.RadarLine("Your " + (det is Radar ? "radar" : "eyes") + " found a " + type + " (" + side + ") at " + (FastMath.Distance(me, it) / 1000f).ToString("0.0", c) + " km, you at " + me.y.ToString("0", c) + " m, it at " + it.y.ToString("0", c) + " m, size on radar " + target.RCS.ToString("0.###", c) + ".");
			}
		}

		private static void OnWarning(Aircraft.OnRadarWarning e)
		{
			if (e.emitter == null) return;
			GlobalPosition p = e.emitter.GlobalPosition();
			warnings.Add(new Warning
			{
				emitter = e.emitter.persistentID.Id,
				x = p.x,
				z = p.z,
				kind = e.isTarget ? "target" : e.detected ? "detected" : "passive",
				time = Time.unscaledTime
			});
			if (warnings.Count > 64) warnings.RemoveAt(0);
			string kind = warnings[warnings.Count - 1].kind;
			FlightLog.Warning(kind);
			if (watched != null && loggedEmitters.Count < 100 && kind != "passive" && loggedEmitters.Add(e.emitter.persistentID.Id))
			{
				string type = e.emitter.definition != null ? e.emitter.definition.unitName : "a unit";
				float d = FastMath.Distance(watched.GlobalPosition(), p) / 1000f;
				FlightLog.RadarLine("Radar warning: a " + type + " " + (kind == "target" ? "locked on you" : "found you") + " from " + d.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km.");
			}
		}

		// Main thread, while the snapshot is built.
		internal static void Fill(MapSnapshot snapshot, Aircraft aircraft)
		{
			if (aircraft == null || aircraft != watched || snapshot.player == null)
			{
				return;
			}
			float now = Time.unscaledTime;
			if (radar != null && radar.IsOperational())
			{
				Transform scanner = radar.GetScanPoint();
				Vector3 f = scanner != null ? scanner.forward : aircraft.transform.forward;
				float cone = RadarCone != null ? (float)RadarCone.GetValue(radar) : 0f;
				if (radar.activated != wasOn) { wasOn = radar.activated; FlightLog.RadarLine("You switched your radar " + (wasOn ? "on" : "off") + "."); }
				bool jammedNow = radar.IsJammed();
				if (jammedNow != wasJammed) { wasJammed = jammedNow; FlightLog.RadarLine(jammedNow ? "Your radar is jammed." : "Your radar is no longer jammed."); }
				snapshot.player.radar = new RadarSnapshot
				{
					on = radar.activated,
					range = radar.RadarParameters.maxRange,
					cone = cone <= 0f || cone >= 180f ? 0f : cone,
					yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg,
					scans = scans,
					jammed = radar.IsJammed()
				};
			}
			var gone = new List<uint>();
			foreach (var kv in finds)
			{
				if (now - kv.Value.lastSeen > ForgetAfter) gone.Add(kv.Key);
			}
			foreach (uint id in gone) finds.Remove(id);
			if (finds.Count > 0)
			{
				snapshot.finds = new List<FindSnapshot>(finds.Count);
				foreach (var kv in finds)
				{
					snapshot.finds.Add(new FindSnapshot { id = kv.Key, pings = kv.Value.pings, radar = kv.Value.radar });
				}
			}
			warnings.RemoveAll(w => now - w.time > WarningLife);
			if (warnings.Count > 0)
			{
				snapshot.radarWarnings = new List<RadarWarningSnapshot>(warnings.Count);
				foreach (Warning w in warnings)
				{
					snapshot.radarWarnings.Add(new RadarWarningSnapshot { emitter = w.emitter, x = w.x, z = w.z, kind = w.kind, age = now - w.time });
				}
			}
		}
	}

	internal class RadarSnapshot
	{
		public bool on;
		public float range;  // the radar's range number (m); real reach depends on the target's size, see FEATURES.md
		public float cone;   // half-angle in degrees, 0 = all round
		public float yaw;    // where the radar looks, degrees like heading
		public int scans;    // scans since you got this aircraft, so the page can flash on each new one
		public bool jammed;
	}

	internal class FindSnapshot
	{
		public uint id;      // a unit in this snapshot (the page draws nothing for an id it does not have)
		public int pings;    // scans of your radar that saw it, 0 = only your eyes
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool radar;
	}

	internal class RadarWarningSnapshot
	{
		public uint emitter;
		public float x;
		public float z;
		public string kind;
		public float age;    // seconds since the warning
	}
}
