using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using NuclearOption.Networking;
using NuclearOption.UI;
using UnityEngine;

namespace NOTacMap
{
	// Stats for the STATS panel on the map: this life (one aircraft), this round (one mission, until Victory or Defeat), all time
	// (a file, singleplayer and multiplayer apart). Read from the same two messages the game uses for its own kill text and
	// kill feed: the kill credit the server sends only to you (MessageManager TargetCreditMessage) and the kill feed every PC gets
	// (MessageManager RpcKillMessage: who did the most damage, what died). The game has no events for them, so a Harmony
	// prefix reads their arguments; it changes nothing. Finds come from OwnRadar.
	internal static class Stats
	{
		internal class Tally
		{
			public int kills, assists, deaths, missiles, friendlyFire, finds, newForTeam, laterDestroyed;
			public Dictionary<string, int> killsByName = new Dictionary<string, int>();   // "kind|unit type"
			public Dictionary<string, int> killedBy = new Dictionary<string, int>();      // unit type that killed you
			public Dictionary<string, int> findsByName = new Dictionary<string, int>();   // "kind|unit type"
		}

		internal class Plane { public int kills, deaths, lives; }
		internal class Best { public int value; public string plane, map, date; }

		internal class AllTime
		{
			public Tally tally = new Tally();
			public Dictionary<string, Plane> planes = new Dictionary<string, Plane>();
			public Best bestLife = new Best(), bestRound = new Best(), bestLifeFinds = new Best();
			public string since;
		}

		internal class SaveFile
		{
			public int version = 1;
			public AllTime mp = new AllTime(), sp = new AllTime();
		}

		private static SaveFile save = new SaveFile();
		private static Tally life, round;
		private static string lifePlane, roundMap, mode = "mp", result;
		private static bool roundActive, roundEnded;
		private static int roundBestLife;
		private static uint myAircraftId, lastAircraftId;
		private static readonly HashSet<uint> lifeFinds = new HashSet<uint>(), roundFinds = new HashSet<uint>();
		private static readonly Dictionary<uint, float> pendingCredit = new Dictionary<uint, float>();
		private static bool dirty;
		private static float nextSave;
		private static int version, endedCount, deathCount;
		private static bool boardOpen;
		private static volatile string json = "{}";

		private static string FilePath => Path.Combine(Paths.ConfigPath, "NOTacMap-stats.json");

		internal static void Init(Harmony harmony)
		{
			Load();
			try
			{
				foreach (MethodInfo m in typeof(MessageManager).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (m.Name.StartsWith("UserCode_RpcKillMessage", StringComparison.Ordinal) && m.GetParameters().Length == 3)
						harmony.Patch(m, prefix: new HarmonyMethod(typeof(Stats).GetMethod(nameof(KillFeedPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
					else if (m.Name.StartsWith("UserCode_TargetCreditMessage", StringComparison.Ordinal) && m.GetParameters().Length == 4)
						harmony.Patch(m, prefix: new HarmonyMethod(typeof(Stats).GetMethod(nameof(CreditPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("NOTacMap: stats could not hook the kill messages, kills will not be counted: " + ex.Message);
			}
			Publish();
		}

		// ---- the game's messages

		// TargetCreditMessage(INetworkPlayer, PersistentID killedID, float creditAwarded, RewardType actionType): you got credit.
		private static void CreditPrefix(PersistentID __1, float __2, FactionHQ.RewardType __3)
		{
			try
			{
				if (__3 == FactionHQ.RewardType.Kill && __1.IsValid) pendingCredit[__1.Id] = Time.unscaledTime;
			}
			catch (Exception) { /* never break the game's message */ }
		}

		// RpcKillMessage(PersistentID killerID, PersistentID killedID, KillType): something died, the killer did the most damage.
		private static void KillFeedPrefix(PersistentID __0, PersistentID __1, KillType __2)
		{
			try { KillFeed(__0, __1, __2); }
			catch (Exception ex) { FlightLog.Error("counting a kill for the stats", ex); }
		}

		private static void KillFeed(PersistentID killerID, PersistentID killedID, KillType type)
		{
			if (!roundActive || !GameManager.GetLocalPlayer<Player>(out var me)) return;
			UnitRegistry.TryGetPersistentUnit(killedID, out PersistentUnit killed);
			UnitRegistry.TryGetPersistentUnit(killerID, out PersistentUnit killer);
			string killedName = killed != null && killed.definition != null ? killed.definition.unitName : "a unit";
			string kind = Kind(type);

			// you died
			if (killedID.IsValid && (killedID.Id == myAircraftId || killedID.Id == lastAircraftId) && myAircraftId != 0)
			{
				string by = killer == null ? "unknown (a crash or no damage)" : (killer.definition != null ? killer.definition.unitName : "unknown") + (killer.player != null && killer.player != me ? " (a player)" : "");
				if (killer != null && killer.player == me) by = "yourself";
				Each(t => { t.deaths++; Add(t.killedBy, by); });
				deathCount++;
				if (lifePlane != null) PlaneOf(lifePlane).deaths++;
				Changed();
				return;
			}

			bool mine = killer != null && killer.player == me;
			bool credited = killedID.IsValid && pendingCredit.Remove(killedID.Id);
			bool sameSide = killed != null && me.HQ != null && killed.GetHQ() == me.HQ;
			if (killedID.IsValid && roundFinds.Contains(killedID.Id) && !sameSide)
			{
				round.laterDestroyed++; Total.tally.laterDestroyed++;
				if (lifeFinds.Contains(killedID.Id)) life.laterDestroyed++;
			}
			if (!mine && !credited) { if (roundFinds.Contains(killedID.Id)) Changed(); return; }
			if (sameSide)
			{
				Each(t => t.friendlyFire++);
			}
			else if (type == KillType.Missile)
			{
				Each(t => t.missiles++);
			}
			else if (mine)
			{
				Each(t => { t.kills++; Add(t.killsByName, kind + "|" + killedName + (killed != null && killed.player != null ? "|player" : "")); });
				if (lifePlane != null) PlaneOf(lifePlane).kills++;
				if (life.kills > roundBestLife) roundBestLife = life.kills;
				BestOf(Total.bestLife, life.kills, true);
			}
			else
			{
				Each(t => t.assists++);
			}
			Changed();
		}

		private static string Kind(KillType t)
		{
			switch (t)
			{
				case KillType.Aircraft: return "aircraft";
				case KillType.Vehicle: return "groundvehicle";
				case KillType.Building: return "building";
				case KillType.Missile: return "missile";
				case KillType.Ship: return "ship";
				default: return "other";
			}
		}

		// From OwnRadar: one of your sensors saw a unit. Counted once per life and once per round, enemies only.
		internal static void Found(Unit target, bool radar)
		{
			if (!roundActive || target == null || !GameManager.GetLocalPlayer<Player>(out var me) || me.HQ == null) return;
			FactionHQ hq = target.NetworkHQ;
			if (hq == null || hq == me.HQ || target is Missile) return;
			uint id = target.persistentID.Id;
			string name = (target.definition != null ? target.definition.unitName : "a unit");
			string kind = target is Aircraft ? "aircraft" : target is Ship ? "ship" : target is Building ? "building" : target is GroundVehicle ? "groundvehicle" : "other";
			bool newForTeam = !me.HQ.trackingDatabase.ContainsKey(target.persistentID);
			bool newInRound = roundFinds.Add(id);
			bool newInLife = lifeFinds.Add(id);
			if (newInLife) { life.finds++; Add(life.findsByName, kind + "|" + name); if (newForTeam) life.newForTeam++; }
			if (newInRound)
			{
				round.finds++; Add(round.findsByName, kind + "|" + name); if (newForTeam) round.newForTeam++;
				Total.tally.finds++; Add(Total.tally.findsByName, kind + "|" + name); if (newForTeam) Total.tally.newForTeam++;
			}
			if (newInLife || newInRound) Changed();
		}

		// ---- rounds and lives, every frame on the main thread
		internal static void Update(Aircraft aircraft)
		{
			try
			{
				GameState state = GameManager.gameState;
				bool inMission = state == GameState.SinglePlayer || state == GameState.Multiplayer;
				GameResolution res = GameManager.gameResolution;
				if (inMission && !roundActive) StartRound(state);
				else if (roundActive && !inMission) CloseRound();
				else if (roundActive && roundEnded && res == GameResolution.Ongoing) { CloseRound(); StartRound(state); }
				if (roundActive && !roundEnded && res != GameResolution.Ongoing) EndRound(res == GameResolution.Victory ? "Victory" : "Defeat");

				uint id = aircraft != null ? aircraft.persistentID.Id : 0;
				if (id != myAircraftId)
				{
					if (myAircraftId != 0) lastAircraftId = myAircraftId;
					myAircraftId = id;
					if (aircraft != null && roundActive) StartLife(aircraft);
				}

				bool open = LeaderboardMenu.IsOpen();
				if (open != boardOpen) { boardOpen = open; Publish(); }

				// credit without a kill feed line for a while: a share of a kill
				if (pendingCredit.Count > 0)
				{
					var old = new List<uint>();
					foreach (var kv in pendingCredit) if (Time.unscaledTime - kv.Value > 10f) old.Add(kv.Key);
					foreach (uint k in old) { pendingCredit.Remove(k); if (roundActive) { Each(t => t.assists++); Changed(); } }
				}

				string reset = pendingReset;
				if (reset != null) { pendingReset = null; DoReset(reset); }
				if (dirty && Time.unscaledTime >= nextSave) Save();
			}
			catch (Exception ex)
			{
				FlightLog.Error("updating the stats", ex);
			}
		}

		private static void StartRound(GameState state)
		{
			mode = state == GameState.Multiplayer ? "mp" : "sp";
			round = new Tally();
			life = new Tally();
			lifePlane = null;
			roundFinds.Clear(); lifeFinds.Clear(); pendingCredit.Clear();
			roundBestLife = 0;
			roundActive = true; roundEnded = false; result = null;
			roundMap = null;
			if (Total.since == null) Total.since = DateTime.Now.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
			Changed();
		}

		private static void StartLife(Aircraft aircraft)
		{
			life = new Tally();
			lifeFinds.Clear();
			lifePlane = aircraft.definition != null ? aircraft.definition.unitName : "an aircraft";
			PlaneOf(lifePlane).lives++;
			Changed();
		}

		private static void EndRound(string how)
		{
			roundEnded = true;
			result = how;
			endedCount++;
			Finish();
			Save();
		}

		// The mission is left (or the next one starts) without, or after, a result.
		private static void CloseRound()
		{
			if (!roundEnded) Finish();
			roundActive = false;
			Save();
		}

		private static void Finish()
		{
			roundMap = (MapImageCapture.MapName ?? "").Replace("(Clone)", "").Trim();
			BestOf(Total.bestRound, round.kills, false);
			BestOf(Total.bestLifeFinds, life.finds, true);
			Changed();
		}

		// ---- helpers
		private static AllTime Total => mode == "sp" ? save.sp : save.mp;

		private static void Each(Action<Tally> f)
		{
			f(life); f(round); f(Total.tally);
		}

		private static void Add(Dictionary<string, int> d, string key)
		{
			d.TryGetValue(key, out int n);
			d[key] = n + 1;
		}

		private static Plane PlaneOf(string name)
		{
			if (!Total.planes.TryGetValue(name, out Plane p)) Total.planes[name] = p = new Plane();
			return p;
		}

		private static void BestOf(Best b, int value, bool withPlane)
		{
			if (value <= b.value) return;
			b.value = value;
			b.plane = withPlane ? lifePlane : null;
			b.map = (MapImageCapture.MapName ?? "").Replace("(Clone)", "").Trim();
			b.date = DateTime.Now.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture);
		}

		private static void Changed()
		{
			dirty = true;
			if (nextSave < Time.unscaledTime) nextSave = Time.unscaledTime + 120f;
			Publish();
		}

		// What the page reads at /stats.
		private static void Publish()
		{
			version++;
			var o = new
			{
				version,
				mode,
				roundActive,
				roundEnded,
				result,
				map = roundMap ?? (MapImageCapture.MapName ?? "").Replace("(Clone)", "").Trim(),
				lifePlane,
				life,
				round,
				roundBestLife,
				all = Total,
				other = mode == "sp" ? save.mp : save.sp
			};
			try { json = JsonConvert.SerializeObject(o); } catch (Exception) { /* keep the last */ }
		}

		internal static string Json() => json;

		// For the snapshot: a small note that tells every map when to read /stats again, when a round ended and if the scoreboard is open.
		internal static StatsNote Note() => new StatsNote { v = version, ended = endedCount, board = boardOpen, died = deathCount };

		// ---- the file: written to a new file, then swapped in, the old one kept as a backup
		private static void Load()
		{
			foreach (string path in new[] { FilePath, FilePath + ".bak" })
			{
				try
				{
					if (!File.Exists(path)) continue;
					SaveFile s = JsonConvert.DeserializeObject<SaveFile>(File.ReadAllText(path));
					if (s != null && s.mp != null && s.sp != null) { save = s; return; }
				}
				catch (Exception ex)
				{
					Plugin.Log.LogWarning("NOTacMap: the stats file " + Path.GetFileName(path) + " could not be read (" + ex.Message + "), trying the backup.");
				}
			}
		}

		internal static void Save()
		{
			if (!dirty) return;
			dirty = false;
			try
			{
				string tmp = FilePath + ".tmp";
				File.WriteAllText(tmp, JsonConvert.SerializeObject(save, Formatting.Indented));
				if (File.Exists(FilePath))
				{
					try { File.Replace(tmp, FilePath, FilePath + ".bak"); }
					catch (Exception) { File.Copy(FilePath, FilePath + ".bak", true); File.Copy(tmp, FilePath, true); File.Delete(tmp); }
				}
				else File.Move(tmp, FilePath);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogWarning("NOTacMap: the stats could not be saved: " + ex.Message);
				dirty = true;
			}
		}

		// From the map page (PC only): start the all-time numbers of one mode again. The old file stays as the backup.
		private static volatile string pendingReset;

		// Web server thread: only noted, done on the main thread in Update.
		internal static string Reset(string which)
		{
			if (which != "mp" && which != "sp") return "Unknown stats.";
			pendingReset = which;
			return null;
		}

		private static void DoReset(string which)
		{
			if (which == "mp") save.mp = new AllTime(); else save.sp = new AllTime();
			Total.since = DateTime.Now.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
			dirty = true;
			Save();
			Publish();
		}

		internal static void Quit()
		{
			if (roundActive && !roundEnded) Finish();
			dirty = true;
			Save();
		}
	}

	internal class StatsNote
	{
		public int v;      // stats changed: read /stats again
		public int ended;  // rounds ended since the game started: opens the panel when it goes up
		public bool board; // the game's scoreboard (Esc) is open
		public int died;   // times you died since the game started (crashes too): the map marks where you lost the aircraft
	}
}
