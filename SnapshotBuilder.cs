using System.Collections.Generic;
using Newtonsoft.Json;
using NuclearOption.Networking;
using UnityEngine;

namespace NOTacMap
{
	// Plain data classes for JSON. Kept separate from game types on purpose:
	// we only ever copy out what the local faction already knows (positions
	// via TrackingInfo, never raw UnitRegistry.allUnits), so a mod restart or
	// a game update changing internal field names can't silently start
	// leaking hidden units through here.
	internal class UnitSnapshot
	{
		public uint id;
		public string name;
		public string type;
		public string baseType; // "aircraft" | "groundvehicle" | "building" | "ship" | "other", a plain C# type check, never wrong, used as a fallback shape when the name isn't in the known-types list
		public string faction; // "friendly" | "enemy" | "neutral" | "spectator"
		// Flags and the target block are left out of the JSON while they're at
		// their default (false/0/null). Most of the several hundred units on a
		// busy server are static structures with none of these set, and the page
		// reads a missing flag as false. The target position is sent only with
		// a lock (ShouldSerialize below), so a real 0 can never go missing.
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool isPlayer; // human-piloted aircraft, vs. an AI/structure on the same faction
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool isIncomingMissile; // locked onto the local player's own aircraft right now
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool isHelicopter; // the game's own pilot type of the aircraft is Helo, so a modded helicopter is no plane even if its name is not in the page's list
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool radarDestroyed; // the unit had a radar and it is destroyed (the radar part is gone, the unit itself is not)
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool jammed; // the unit's radar is jammed right now (Radar.IsJammed), the same flag the game's own map shows as JAMMED
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public float? jamAge; // jammed units only: seconds since the last jam event (a radar stays jammed for a few seconds after the jamming stops)
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public uint? jammedBy; // the unit that jammed it last, only sent when that unit is in this snapshot too (the game's map draws the line only then)
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public uint? shooterId; // an incoming missile only: the unit that fired it, sent only when that unit is in this snapshot too
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public float blastRadius; // a nuclear missile or bomb only: the radius of its blast in meters, the game's own Shockwave formula (13 x the cube root of the yield)
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool isMissile; // any missile or bomb, any faction, lets the client tell enemy ones apart for trail filtering
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool isMyMissile; // a missile the local player personally launched
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool isFriendlyMissile; // any friendly-faction missile, mine or a teammate's
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ownerName; // isFriendlyMissile only: pilot's real display name, null if AI-flown or unowned
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string ownerSteamId; // isFriendlyMissile only: stable key for the client's callsign assignment, GetDisplayName() can change format (e.g. a "[3] " player-index prefix) across a respawn depending on THAT player's own settings, which would otherwise look like a "new" name and get a fresh random callsign
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool hasTarget; // isFriendlyMissile only: it has a live lock
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool targetIsAircraft; // hasTarget only: air-to-air vs air-to-ground, for client-side filtering
		public float targetX;
		public float targetY;
		public float targetZ;
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string targetName;
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public uint targetId; // hasTarget only: lets the page match locks to a specific target instead of by name
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public bool targetDestroyed; // missile's target is dead/gone, a kill, not a lost lock
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public float speedKmh; // missiles only (real Rigidbody.velocity.magnitude): the page needs it for the ETA, and the game's own map shows no speed for any other unit
		public float x;
		public float y;
		public float z;
		public float heading; // transform.eulerAngles.y, which way the icon should point on the map

		// Newtonsoft calls these by name: the target block only goes out for a
		// unit that actually has a lock (targetId/targetName are covered by the
		// default/null handling, 0 and null both mean "none").
		public bool ShouldSerializetargetX() { return hasTarget; }
		public bool ShouldSerializetargetY() { return hasTarget; }
		public bool ShouldSerializetargetZ() { return hasTarget; }
	}

	internal class AirbaseSnapshot
	{
		public string name;
		public string faction;
		public float x;
		public float z;
		public bool hangarsAvailable;
		public List<RunwaySnapshot> runways = new List<RunwaySnapshot>();
	}

	internal class RunwaySnapshot
	{
		// Real threshold positions (Runway.Start/End), not a guess at heading -
		// the client draws the extended centerline by projecting past these.
		public float startX;
		public float startZ;
		public float endX;
		public float endZ;
	}

	internal class WaypointSnapshot
	{
		public float x;
		public float z;
	}

	internal class MarkedTargetSnapshot
	{
		// Your own pre-fire target selection (Aircraft.weaponManager.GetTargetList()).
		// That is a plain local List<Unit> on WeaponManager, never networked, so it
		// is only ever your own; there's no way to read a teammate's or an AI's.
		public uint id; // so the client can remember where each one was last tracked
		public bool lost; // still in the marked list, but not currently spotted. With a position and name it's the last-known spot (stale); with neither, the tracker has nothing at all
		public float lostFor; // seconds since it was last spotted, for the stale case
		public float x;
		public float y;
		public float z;
		public string name;
		public bool isPrimary; // targetList[0], what actually fires first
	}

	internal class WeaponRangeSnapshot
	{
		// Your currently selected weapon station (WeaponManager.currentWeaponStation).
		// maxRange is the static baseline (TargetRequirements.maxRange) until a
		// target is marked, at which point it's replaced by a live figure from
		// the weapon's own Missile.CalcRange(), the exact call the cockpit's
		// own HUD uses, factoring current speed/altitude and the target's.
		public float maxRange;
		public float minAlignment; // degrees off nose, TargetRequirements.minAlignment, real per-weapon value
		public bool armed; // false = static circle, true = live cone toward a marked target
	}

	// One of your own weapon stations, for the Combat view buttons. Your own aircraft's data only.
	// air, surface and radar are WeaponInfo.effectiveness (0 to 1, how well the weapon works against that kind of target).
	internal class LoadoutStation
	{
		public int n; // WeaponStation.Number
		public string name;
		public float air;
		public float surface;
		public float radar;
		public bool gun;
		public bool bomb; // a bomb or glide bomb
		public int ammo;
		public bool selected;
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public float blast; // a nuclear weapon only: the radius of its blast in meters
		[JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)] public float reach; // an unguided bomb that is not nuclear: how far its blast and fragments reach in meters
	}

	internal class PlayerSnapshot
	{
		public string type; // same unitName used for other units' classification, so the client can tell a heli apart from a plane for its own icon too
		public bool isHelicopter; // see UnitSnapshot.isHelicopter
		public uint id; // your aircraft's id, so the page can tell which jammed units are jammed by you
		public float jam; // the cockpit's own jamming strength (CombatHUD.jamAccumulation), 0 when you are not jammed
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public uint? jammedBy; // who jams you, same rule as UnitSnapshot.jammedBy
		public float speedKmh; // real Rigidbody.velocity.magnitude, see FlightHud.cs's own cockpitRB.velocity.magnitude
		public float x;
		public float y;
		public float z;
		public float heading;
	}

	// Where an unguided bomb of your selected station would hit if you let it go now, worked out the way the cockpit's own bombing display does it.
	internal class BombImpactSnapshot
	{
		public float x;
		public float y;
		public float z;
		public float fallTime; // seconds the bomb needs from your altitude, the cockpit's ToF
	}

	// A nuclear weapon that really went off: the game's missile was armed when it ended (a shot down or timed out one that was not armed only
	// fizzles, and one that is just out of sight is not an explosion either). Sent for a few seconds so a slow page does not miss it.
	internal class BlastSnapshot
	{
		public uint id;
		public float x;
		public float z;
		public float r;
		public bool friendly;
	}

	internal class MapSnapshot
	{
		// Server-side timestamp (seconds since game start). A background browser tab
		// gets its events batched and throttled, so the arrival time on the client is
		// not a reliable measure of the time between two snapshots. Use deltas of this.
		public double t;
		public string unitSystem; // the game's own Gameplay setting: "metric" or "imperial"
		public PlayerSnapshot player;
		public List<UnitSnapshot> units = new List<UnitSnapshot>();
		public List<AirbaseSnapshot> airbases = new List<AirbaseSnapshot>();
		public List<WaypointSnapshot> waypoints = new List<WaypointSnapshot>();
		public List<MarkedTargetSnapshot> markedTargets = new List<MarkedTargetSnapshot>();
		public WeaponRangeSnapshot weaponRange;
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
		public List<BlastSnapshot> blasts;
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
		public BombImpactSnapshot bombImpact; // your own selected unguided bomb only
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
		public List<LoadoutStation> loadout; // your own weapon stations, only while you are in an aircraft
		[JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
		public KeyPresses keys; // the map keys from the config, counted since the game started; missing until one was pressed
	}

	internal class KeyPresses
	{
		public int combat;
		public int zoomIn;
		public int zoomOut;
	}

	internal static class SnapshotBuilder
	{
		// Jamming. The game tells every client about each jam event on a unit (Unit.onJam, with the jammer), and its own map shows
		// a JAMMED tag and a line to the jammer for units whose radar is jammed. The last jammer is remembered per unit. The
		// note lives on the unit itself, so nothing is left behind when the unit is destroyed.
		private class JamNote
		{
			public uint jammer;
			public float time; // Time.realtimeSinceStartup of the last jam event
		}

		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Unit, JamNote> jamNotes = new System.Runtime.CompilerServices.ConditionalWeakTable<Unit, JamNote>();
		private static bool jamErrorLogged;

		private static JamNote JamNoteOf(Unit unit)
		{
			if (jamNotes.TryGetValue(unit, out JamNote note))
			{
				return note;
			}
			note = new JamNote();
			jamNotes.Add(unit, note);
			JamNote remembered = note;
			unit.onJam += args =>
			{
				if (args.jammingUnit != null)
				{
					remembered.jammer = args.jammingUnit.persistentID.Id;
					remembered.time = Time.realtimeSinceStartup;
				}
			};
			return note;
		}

		// Fills the jamming flags of one unit. A mistake in here must not take the whole map down, so it is caught and logged once.
		private static void FillJamming(Unit unit, UnitSnapshot snap)
		{
			try
			{
				// A destroyed radar is a Unity "fake null": the reference is still there but the object is gone. It is reported as destroyed, and
				// it is left out of the jamming: it can no longer wear the jamming off, so it would stay jammed for good (the game's own
				// JammedMarker drops its tag in the same case).
				TargetDetector detector = unit.radar;
				if ((object)detector != null && detector == null)
				{
					snap.radarDestroyed = true;
					return;
				}
				if (unit.radar is Radar radar && radar != null)
				{
					JamNote note = JamNoteOf(unit);
					if (radar.IsJammed())
					{
						snap.jammed = true;
						snap.jamAge = Round1(Time.realtimeSinceStartup - note.time);
						if (note.jammer != 0)
						{
							snap.jammedBy = note.jammer;
						}
					}
				}
			}
			catch (System.Exception ex)
			{
				if (!jamErrorLogged)
				{
					jamErrorLogged = true;
					Plugin.Log.LogWarning("NOTacMap: reading the jamming of a unit failed, jamming is left out for it: " + ex.Message);
				}
			}
		}

		private static void FillPlayerJamming(Aircraft aircraft, PlayerSnapshot player)
		{
			try
			{
				player.id = aircraft.persistentID.Id;
				JamNote note = JamNoteOf(aircraft);
				CombatHUD hud = SceneSingleton<CombatHUD>.i;
				player.jam = hud != null ? (float)System.Math.Round(hud.jamAccumulation, 2) : 0f;
				if (player.jam > 0f && note.jammer != 0)
				{
					player.jammedBy = note.jammer;
				}
			}
			catch (System.Exception ex)
			{
				if (!jamErrorLogged)
				{
					jamErrorLogged = true;
					Plugin.Log.LogWarning("NOTacMap: reading your jamming failed, jamming is left out: " + ex.Message);
				}
			}
		}

		// The game's own pilot type of an aircraft (the AI picks its helicopter flying from it, so a modded helicopter has it set
		// right). Tiltwing and VTOL stay planes. The first time a kind of aircraft is seen its type goes in the log, so a
		// wrong guess is easy to find.
		private static readonly System.Collections.Generic.HashSet<string> loggedPilotTypes = new System.Collections.Generic.HashSet<string>();

		private static bool IsHelicopter(Aircraft aircraft)
		{
			Pilot[] pilots = aircraft.pilots;
			if (pilots == null || pilots.Length == 0 || pilots[0] == null)
			{
				return false;
			}
			Pilot.PilotType pilotType = pilots[0].pilotType;
			string name = aircraft.definition != null ? aircraft.definition.unitName : aircraft.GetType().Name;
			if (loggedPilotTypes.Add(name))
			{
				Plugin.Log.LogInfo($"NOTacMap: aircraft '{name}' has pilot type {pilotType}.");
			}
			return pilotType == Pilot.PilotType.Helo;
		}

		private static float Round1(float value)
		{
			return Mathf.Round(value * 10f) / 10f;
		}

		// The impact point of an unguided bomb. This is HUDBombingState.CCIPTrajectory and SplitSearchTrajectory of the game, so the point is
		// where the cockpit's own pipper is: the bomb leaves with the aircraft's speed (less a quarter of gravity for a second), 32 steps of
		// growing length with drag from the fin area, mass and air density, then the first hit of a line cast along the path, or the sea.
		private sealed class BombShape
		{
			public float dragCoef;
			public float finArea;
			public float mass;
		}

		private static readonly Dictionary<WeaponInfo, BombShape> bombShapes = new Dictionary<WeaponInfo, BombShape>();
		private static readonly List<GlobalPosition> bombPath = new List<GlobalPosition>(40);
		private static bool bombErrorLogged;

		private static BombImpactSnapshot BombImpactOf(Aircraft aircraft, WeaponStation station)
		{
			try
			{
				WeaponInfo info = station != null ? station.WeaponInfo : null;
				if (info == null || !info.bomb || info.glideBomb || info.gun || station.Ammo <= 0 || aircraft.gearDeployed || aircraft.rb == null)
				{
					return null;
				}
				if (!bombShapes.TryGetValue(info, out BombShape shape))
				{
					Missile prefab = info.weaponPrefab != null ? info.weaponPrefab.GetComponent<Missile>() : null;
					shape = prefab == null ? null : new BombShape { dragCoef = prefab.GetDragCoef(Mathf.PI / 360f), finArea = prefab.GetFinArea(), mass = info.massPerRound };
					bombShapes[info] = shape;
				}
				if (shape == null || shape.mass <= 0f)
				{
					return null;
				}

				GlobalPosition position = aircraft.GlobalPosition() - Vector3.up * aircraft.definition.spawnOffset.y;
				Vector3 velocity = aircraft.rb.velocity - Vector3.up * 9.81f * 0.25f + info.muzzleVelocity * aircraft.transform.forward;
				float step = 0.5f;
				float drag = 0.5f * shape.dragCoef * LevelInfo.GetAirDensity(aircraft.GlobalPosition().y) * shape.finArea / shape.mass;
				bombPath.Clear();
				bombPath.Add(position);
				bool reachedSea = false;
				for (int i = 0; i < 32; i++)
				{
					step += 0.04f;
					GlobalPosition before = position;
					position += velocity * step;
					if (position.y < 0f)
					{
						// the path ends where it crosses the sea
						float t = before.y / (before.y - position.y);
						bombPath.Add(before + (position - before) * Mathf.Clamp01(t));
						reachedSea = true;
						break;
					}
					bombPath.Add(position);
					velocity -= (Vector3.up * 9.81f + velocity.normalized * drag * velocity.sqrMagnitude) * step;
				}
				if (!reachedSea)
				{
					return null;   // it would still be in the air after 32 steps: no impact to show, like the cockpit
				}

				// the first thing the path runs into on the way down, else the sea at its end
				GlobalPosition impact = bombPath[bombPath.Count - 1];
				for (int i = 1; i < bombPath.Count; i++)
				{
					if (Physics.Linecast(bombPath[i - 1].ToLocalPosition(), bombPath[i].ToLocalPosition(), out RaycastHit hit, ~(int)PhysicsLayers.ExclusionZonesMask))
					{
						impact = hit.point.ToGlobalPosition();
						break;
					}
				}
				return new BombImpactSnapshot
				{
					x = Round1(impact.x),
					y = Round1(impact.y),
					z = Round1(impact.z),
					fallTime = Round1(Kinematics.FallTime(aircraft.radarAlt, aircraft.rb.velocity.y))
				};
			}
			catch (System.Exception ex)
			{
				if (!bombErrorLogged)
				{
					bombErrorLogged = true;
					Plugin.Log.LogWarning("NOTacMap: working out where your bomb would land failed, the impact point is left out: " + ex.Message);
				}
				return null;
			}
		}

		// The game itself treats a warhead of more than 100000 as a nuclear one (Missile.CheckExclusionZone keeps the AI out of a zone
		// around its target), and its Shockwave works with a blast radius of 13 x the cube root of the yield, the same numbers are used here.
		private const float NukeYield = 100000f;

		internal static float BlastRadiusOf(float yield)
		{
			return yield > NukeYield ? Mathf.Round(Mathf.Pow(yield, 0.3333f) * 13f / 10f) * 10f : 0f;
		}

		// How far the blast and fragments of an ordinary bomb reach: the game's DamageEffects.BlastFrag looks for things to hurt within 20 x the
		// cube root of the yield. It is the furthest the bomb can hurt, not where everything is destroyed.
		internal static float ReachOf(float yield)
		{
			return yield > 0f && yield <= NukeYield ? Mathf.Round(Mathf.Pow(yield, 0.3333f) * 20f) : 0f;
		}

		private static readonly Dictionary<WeaponInfo, float> weaponReach = new Dictionary<WeaponInfo, float>();

		private static float ReachOfWeapon(WeaponInfo info)
		{
			if (info == null || !info.bomb || info.glideBomb)
			{
				return 0f;
			}
			if (!weaponReach.TryGetValue(info, out float reach))
			{
				Missile prefab = info.weaponPrefab != null ? info.weaponPrefab.GetComponent<Missile>() : null;
				reach = prefab != null ? ReachOf(prefab.GetYield()) : 0f;
				weaponReach[info] = reach;
				Plugin.Log.LogInfo($"NOTacMap: bomb '{info.weaponName}': blast reach {reach} m.");
			}
			return reach;
		}

		// Nuclear missiles that are on the map: where each was last seen and whether its warhead is armed, so that the end of one can be told
		// from an out of sight one. A missile that is disabled (the game does that the moment it detonates, shot down or not) or gone has ended.
		private sealed class NukeWatch
		{
			public Missile missile;
			public float x, z, radius;
			public bool friendly;
			public bool armed;
			public float lastSeen;
			public float endedAt = -1f;
		}

		private static readonly HashSet<int> shockwavesClaimed = new HashSet<int>();
		private const float ShockwaveWaitSeconds = 1.5f;
		private const float ShockwaveNear = 600f;

		private static readonly Dictionary<uint, NukeWatch> nukeWatches = new Dictionary<uint, NukeWatch>();
		private static readonly List<uint> nukeEnded = new List<uint>();
		private static readonly List<BlastSnapshot> recentBlasts = new List<BlastSnapshot>();
		private static readonly List<float> recentBlastTimes = new List<float>();
		private static uint blastCounter;
		private const float BlastSendSeconds = 6f;
		private const float NukeLostSeconds = 20f;

		private static void WatchNuke(uint id, Missile missile, float x, float z, float radius, bool friendly)
		{
			if (!nukeWatches.TryGetValue(id, out NukeWatch w))
			{
				w = new NukeWatch();
				nukeWatches[id] = w;
			}
			w.missile = missile;
			w.x = x;
			w.z = z;
			w.radius = radius;
			w.friendly = friendly;
			w.armed = missile.IsArmed();
			w.lastSeen = Time.realtimeSinceStartup;
		}

		// Called every update after the units are built: turns the ended nuclear missiles into blasts and keeps the blasts of the last seconds.
		private static void UpdateBlasts(MapSnapshot snapshot)
		{
			float now = Time.realtimeSinceStartup;
			nukeEnded.Clear();
			foreach (KeyValuePair<uint, NukeWatch> entry in nukeWatches)
			{
				NukeWatch w = entry.Value;
				Missile m = w.missile;
				bool gone = m == null;
				if (!gone && !m.disabled)
				{
					if (now - w.lastSeen > NukeLostSeconds)
					{
						nukeEnded.Add(entry.Key);   // out of sight for a long time: nothing to say about it
					}
					continue;
				}
				if (gone)
				{
					// destroyed without being seen disabled first: a mission ending or a long hitch, not something to report as an explosion
					nukeEnded.Add(entry.Key);
					continue;
				}
				// It has ended (the game disables a missile when it detonates, shot down or not). Whether it really went off is what the game
				// shows: a nuclear detonation makes a Shockwave object at its place. The warhead's armed flag alone says nothing (it is on for
				// nearly every warhead), so it is only written to the log. The shockwave can come a moment later, so the plugin looks for it for
				// a second and a half.
				if (w.endedAt < 0f)
				{
					w.endedAt = now;
				}
				GlobalPosition at = m.GlobalPosition();
				Shockwave found = null;
				foreach (Shockwave wave in UnityEngine.Object.FindObjectsOfType<Shockwave>())
				{
					if (wave == null || shockwavesClaimed.Contains(wave.GetInstanceID()))
					{
						continue;
					}
					if (FastMath.Distance(wave.transform.GlobalPosition(), at) < ShockwaveNear)
					{
						found = wave;
						break;
					}
				}
				if (found != null)
				{
					GlobalPosition place = found.transform.GlobalPosition();
					if (shockwavesClaimed.Count > 64)
					{
						shockwavesClaimed.Clear();
					}
					shockwavesClaimed.Add(found.GetInstanceID());
					Plugin.Log.LogInfo($"NOTacMap: a nuclear missile ended and the game made a shockwave there: it went off (warhead armed flag {(w.armed || m.IsArmed())}).");
					recentBlasts.Add(new BlastSnapshot { id = ++blastCounter, x = Round1(place.x), z = Round1(place.z), r = w.radius, friendly = w.friendly });
					recentBlastTimes.Add(now);
					nukeEnded.Add(entry.Key);
				}
				else if (now - w.endedAt > ShockwaveWaitSeconds)
				{
					Plugin.Log.LogInfo($"NOTacMap: a nuclear missile ended and the game made no shockwave: it did not go off (warhead armed flag {(w.armed || m.IsArmed())}).");
					nukeEnded.Add(entry.Key);
				}
			}
			foreach (uint id in nukeEnded)
			{
				nukeWatches.Remove(id);
			}
			for (int i = recentBlasts.Count - 1; i >= 0; i--)
			{
				if (now - recentBlastTimes[i] > BlastSendSeconds)
				{
					recentBlasts.RemoveAt(i);
					recentBlastTimes.RemoveAt(i);
				}
			}
			if (recentBlasts.Count > 0)
			{
				snapshot.blasts = new List<BlastSnapshot>(recentBlasts);
			}
		}

		// Per kind of weapon: the prefab's yield does not change, so it is looked up once.
		private static readonly Dictionary<WeaponInfo, float> weaponBlast = new Dictionary<WeaponInfo, float>();

		private static float BlastOfWeapon(WeaponInfo info)
		{
			if (info == null)
			{
				return 0f;
			}
			if (!weaponBlast.TryGetValue(info, out float radius))
			{
				Missile prefab = info.weaponPrefab != null ? info.weaponPrefab.GetComponent<Missile>() : null;
				radius = prefab != null ? BlastRadiusOf(prefab.GetYield()) : 0f;
				weaponBlast[info] = radius;
				if (radius > 0f)
				{
					Plugin.Log.LogInfo($"NOTacMap: '{info.weaponName}' is a nuclear weapon, blast radius {radius} m.");
				}
			}
			return radius;
		}

		// Weapon.CalcRange() runs an actual iterative drag/ballistics sim, the
		// cockpit HUD (HUDMissileState) only calls it once a second and holds
		// the last result in between, so we do the same instead of recomputing
		// it every 0.2s snapshot tick for no benefit.
		private static float lastWeaponRangeCalcTime;
		private static float lastLiveMaxRange;
		// Last heading sent for each tracked unit. The game's own map only turns an icon while the
		// contact is observed, so for a contact that is no longer observed the heading stays at the
		// last observed value instead of following the unit's real heading.
		private static readonly Dictionary<uint, float> lastHeadings = new Dictionary<uint, float>();
		// Build() runs on the main thread only, one at a time, so its working sets are kept and cleared instead of made again.
		private static readonly HashSet<PersistentID> seenIds = new HashSet<PersistentID>();
		private static readonly HashSet<uint> builtUnitIds = new HashSet<uint>();
		private static readonly HashSet<PersistentID> incomingIds = new HashSet<PersistentID>();

		private static string FactionString(FactionHQ hq)
		{
			switch (DynamicMap.GetFactionMode(hq, checkNoFactionBeforeSpectator: true))
			{
				case FactionMode.Friendly:
					return "friendly";
				case FactionMode.Enemy:
					return "enemy";
				case FactionMode.Spectator:
					return "spectator";
				default:
					return "neutral";
			}
		}

		// Returns null when there is nothing sensible to report yet (main menu, hangar,
		// loading screen). Returns the plain MapSnapshot, not a JSON string: building it
		// needs the Unity API and runs on the main thread, while the JSON serialization
		// is plain CPU work and runs off it.
		public static MapSnapshot Build()
		{
			if (DynamicMap.i == null)
			{
				nukeWatches.Clear();
				recentBlasts.Clear();
				recentBlastTimes.Clear();
				return null;
			}

			var snapshot = new MapSnapshot();
			snapshot.t = Time.realtimeSinceStartupAsDouble;
			snapshot.unitSystem = PlayerSettings.unitSystem == PlayerSettings.UnitSystem.Metric ? "metric" : "imperial";
			Aircraft playerAircraft = null;

			if (GameManager.GetLocalPlayer<Player>(out var localPlayer) && localPlayer.Aircraft != null && !localPlayer.Aircraft.disabled)
			{
				playerAircraft = localPlayer.Aircraft;
				GlobalPosition pos = playerAircraft.GlobalPosition();
				snapshot.player = new PlayerSnapshot
				{
					type = playerAircraft.definition != null ? playerAircraft.definition.unitName : playerAircraft.GetType().Name,
					isHelicopter = IsHelicopter(playerAircraft),
					speedKmh = playerAircraft.rb != null ? playerAircraft.rb.velocity.magnitude * 3.6f : 0f,
					x = pos.x,
					y = pos.y,
					z = pos.z,
					heading = playerAircraft.transform.eulerAngles.y
				};
				FillPlayerJamming(playerAircraft, snapshot.player);
				UnitIconCapture.Want(playerAircraft.definition);

				if (playerAircraft.weaponManager != null)
				{
					List<Unit> targets = playerAircraft.weaponManager.GetTargetList();
					for (int i = 0; i < targets.Count; i++)
					{
						Unit target = targets[i];
						if (target == null || target.disabled)
						{
							continue;
						}
						// Only where your own faction's tracker still has it, same as the
						// cockpit HUD. The marked list can keep a target after contact is
						// lost, and its real position (GlobalPosition) would keep showing
						// where it actually is after the game itself stopped showing it.
						if (!playerAircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition targetPos))
						{
							snapshot.markedTargets.Add(new MarkedTargetSnapshot
							{
								id = target.persistentID.Id,
								lost = true,
								isPrimary = i == 0
							});
							continue;
						}
						// A tracker entry can be stale: TrackingInfo.Observed() is only
						// true if the unit was spotted in the last few seconds, and after
						// that GetPosition() just returns a frozen last-known position. So
						// "has an entry" isn't enough to call it tracked, that frozen
						// point is where it was last seen, and it's flagged as lost.
						bool observed = target.NetworkHQ == playerAircraft.NetworkHQ
							|| (playerAircraft.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo info) && info.Observed());
						float lostFor = 0f;
						if (!observed && playerAircraft.NetworkHQ.trackingDatabase.TryGetValue(target.persistentID, out TrackingInfo staleInfo))
						{
							lostFor = Mathf.Max(0f, Time.timeSinceLevelLoad - staleInfo.lastSpottedTime);
						}
						snapshot.markedTargets.Add(new MarkedTargetSnapshot
						{
							id = target.persistentID.Id,
							lost = !observed,
							lostFor = lostFor,
							x = targetPos.x,
							y = targetPos.y,
							z = targetPos.z,
							name = target.unitName,
							isPrimary = i == 0
						});
					}

					WeaponStation station = playerAircraft.weaponManager.currentWeaponStation;

					snapshot.loadout = new List<LoadoutStation>();
					foreach (WeaponStation loadoutStation in playerAircraft.weaponStations)
					{
						WeaponInfo info = loadoutStation?.WeaponInfo;
						if (info == null || loadoutStation.Cargo || info.cargo)
						{
							continue;
						}
						// Flares, jammers and the like have no effectiveness against anything: not a weapon here.
						if (info.effectiveness.antiAir <= 0f && info.effectiveness.antiSurface <= 0f && info.effectiveness.antiRadar <= 0f && !info.gun)
						{
							continue;
						}
						snapshot.loadout.Add(new LoadoutStation
						{
							n = loadoutStation.Number,
							name = string.IsNullOrEmpty(info.shortName) ? info.weaponName : info.shortName,
							air = Mathf.Round(info.effectiveness.antiAir * 100f) / 100f,
							surface = Mathf.Round(info.effectiveness.antiSurface * 100f) / 100f,
							radar = Mathf.Round(info.effectiveness.antiRadar * 100f) / 100f,
							gun = info.gun,
							bomb = info.bomb || info.glideBomb,
							ammo = loadoutStation.Ammo,
							selected = loadoutStation == station,
							blast = BlastOfWeapon(info),
							reach = ReachOfWeapon(info)
						});
					}
					if (station != null && station.WeaponInfo.gun == false && station.WeaponInfo.targetRequirements.maxRange > 0f)
					{
						TargetRequirements req = station.WeaponInfo.targetRequirements;
						var rangeSnap = new WeaponRangeSnapshot
						{
							maxRange = req.maxRange,
							minAlignment = req.minAlignment,
							armed = false
						};

						Missile prefabMissile = station.WeaponInfo.weaponPrefab != null
							? station.WeaponInfo.weaponPrefab.GetComponent<Missile>()
							: null;

						if (targets.Count > 0 && prefabMissile != null)
						{
							// realtimeSinceStartup and not timeSinceLevelLoad: the level clock starts again at zero on a
							// new mission, and then this waited until it caught up with the old value (the range froze).
							if (Time.realtimeSinceStartup - lastWeaponRangeCalcTime >= 1f)
							{
								lastWeaponRangeCalcTime = Time.realtimeSinceStartup;
								Unit farTarget = null;
								GlobalPosition farPos = default;
								float farDist = 0f;
								foreach (Unit target in targets)
								{
									if (target == null || target.disabled)
									{
										continue;
									}
									if (!playerAircraft.NetworkHQ.TryGetKnownPosition(target, out GlobalPosition targetPos))
									{
										continue;
									}
									float dist = FastMath.Distance(targetPos, pos);
									if (dist > farDist)
									{
										farDist = dist;
										farPos = targetPos;
										farTarget = target;
									}
								}
								if (farTarget != null)
								{
									lastLiveMaxRange = prefabMissile.CalcRange(
										playerAircraft.speed, pos.y, farPos.y, farDist, farTarget.speed, out _);
								}
							}
							rangeSnap.maxRange = lastLiveMaxRange;
							rangeSnap.armed = true;
						}

						snapshot.weaponRange = rangeSnap;
					}
					snapshot.bombImpact = BombImpactOf(playerAircraft, station);
				}
			}

			if (GameManager.GetLocalHQ(out FactionHQ localHq))
			{
				var seen = seenIds;
				seen.Clear();
				var builtIds = builtUnitIds;
				builtIds.Clear();

				// Missiles the local player's own RWR has already promoted from "unknown" to
				// "known" (in range and line of sight, or gated by seeker mode / network
				// share), see MissileWarning.Update(). This is the cockpit RWR's own fairness
				// gate. Scanning Missile.targetID directly would reveal a launch before the
				// player's RWR warns them.
				var incomingMissileIds = incomingIds;
				incomingMissileIds.Clear();
				if (playerAircraft != null)
				{
					MissileWarning warning = playerAircraft.GetMissileWarningSystem();
					if (warning != null)
					{
						foreach (Missile missile in warning.knownMissiles)
						{
							if (missile != null)
							{
								incomingMissileIds.Add(missile.persistentID);
							}
						}
					}
				}

				void AddUnit(PersistentID id)
				{
					if (!seen.Add(id))
					{
						return;
					}
					if (!UnitRegistry.TryGetUnit(id, out Unit unit) || unit == null || unit.disabled)
					{
						return;
					}

					GlobalPosition pos;
					bool hasTrack = localHq.trackingDatabase.TryGetValue(id, out TrackingInfo trackingInfo);
					if (hasTrack)
					{
						pos = trackingInfo.GetPosition();
					}
					else
					{
						pos = unit.GlobalPosition();
					}

					// The same test the game's own map icon uses before it turns to the unit's heading.
					bool observed = !hasTrack || trackingInfo.Observed();
					float heading;
					if (observed)
					{
						heading = Round1(unit.transform.eulerAngles.y);
						lastHeadings[id.Id] = heading;
					}
					else if (!lastHeadings.TryGetValue(id.Id, out heading))
					{
						heading = 0f;
					}
					string faction = FactionString(unit.NetworkHQ);

					var snap = new UnitSnapshot
					{
						id = id.Id,
						name = unit.unitName,
						type = unit.definition != null ? unit.definition.unitName : unit.GetType().Name,
						isHelicopter = unit is Aircraft heloCheck && IsHelicopter(heloCheck),
						baseType = unit is Aircraft ? "aircraft"
							: unit is GroundVehicle ? "groundvehicle"
							: unit is Building ? "building"
							: unit is Ship ? "ship"
							: unit is PilotDismounted ? "pilot"
							: "other",
						faction = faction,
						// Only your own side is shown as human or AI, so an enemy's is not sent.
						isPlayer = faction == "friendly" && unit is Aircraft occupiedAircraft && occupiedAircraft.Player != null,
						isIncomingMissile = incomingMissileIds.Contains(id),
						// Rounded to a tenth: at map scale a tenth of a metre is
						// invisible, but it keeps every number short in the JSON.
						speedKmh = (unit is Missile || (faction == "friendly" && unit is Aircraft)) && unit.rb != null ? Round1(unit.rb.velocity.magnitude * 3.6f) : 0f,
						x = Round1(pos.x),
						y = Round1(pos.y),
						z = Round1(pos.z),
						heading = heading
					};

					if (unit is Missile missile)
					{
						snap.isMissile = true;
						snap.blastRadius = BlastRadiusOf(missile.GetYield());
						if (snap.blastRadius > 0f)
						{
							WatchNuke(id.Id, missile, snap.x, snap.z, snap.blastRadius, faction == "friendly");
						}
						snap.isMyMissile = playerAircraft != null && missile.owner == playerAircraft;
						// Any friendly missile is exactly as visible to a teammate as any
						// other friendly unit already is via trackingDatabase/factionUnits -
						// there's no separate in-game privacy boundary to respect here, so
						// no opt-in/sharing system is needed, just widen the same detection.
						snap.isFriendlyMissile = snap.faction == "friendly";
						if (snap.isIncomingMissile && missile.owner != null)
						{
							snap.shooterId = missile.owner.persistentID.Id;
						}
						if (snap.isFriendlyMissile && missile.owner is Aircraft ownerAircraft && ownerAircraft.Player != null)
						{
							snap.ownerName = ownerAircraft.Player.GetDisplayName(PlayerNameContext.Other);
							snap.ownerSteamId = ownerAircraft.Player.CSteamID.ToString();
						}
						// !target.disabled matters: without it, a missile whose target was
						// destroyed by someone else while still in flight would keep
						// reporting a stale lock on a dead unit's last-known position.
						// Known position, not the target's real one: a lock on a unit
						// your faction no longer tracks shouldn't reveal where it is.
						if (missile.targetID.TryGetUnit(out Unit target) && target != null && !target.disabled
							&& localHq.TryGetKnownPosition(target, out GlobalPosition targetPos))
						{
							snap.hasTarget = true;
							snap.targetIsAircraft = target is Aircraft;
							snap.targetX = targetPos.x;
							snap.targetY = targetPos.y;
							snap.targetZ = targetPos.z;
							snap.targetName = target.unitName;
							snap.targetId = target.persistentID.Id;
						}
						else if (missile.targetID != PersistentID.None
							&& (!missile.targetID.TryGetUnit(out Unit goneTarget) || goneTarget == null || goneTarget.disabled))
						{
							// Still pointed at a unit that's destroyed (or gone). Lets the page
							// tell a kill apart from a seeker that dropped its lock, that one
							// clears targetID itself (Missile.SetTarget(null)).
							snap.targetDestroyed = true;
						}
					}

					FillJamming(unit, snap);
					if (!(unit is Missile) && !(unit is Building))
					{
						UnitIconCapture.Want(unit.definition);
					}
					snapshot.units.Add(snap);
					builtIds.Add(id.Id);
				}

				foreach (KeyValuePair<PersistentID, TrackingInfo> entry in localHq.trackingDatabase)
				{
					AddUnit(entry.Key);
				}
				foreach (PersistentID id in localHq.factionUnits)
				{
					AddUnit(id);
				}
				foreach (PersistentID id in incomingMissileIds)
				{
					// Not necessarily present in trackingDatabase/factionUnits
					// (an enemy-launched missile locked onto you isn't "your"
					// unit), add it explicitly so it doesn't go missing.
					AddUnit(id);
				}

				// A few of the icons that are waiting get cut out of the game's textures.
				UnitIconCapture.Process();

				// A shooter that is not in this snapshot cannot be marked.
				foreach (UnitSnapshot incoming in snapshot.units)
				{
					if (incoming.shooterId.HasValue && !builtIds.Contains(incoming.shooterId.Value))
					{
						incoming.shooterId = null;
					}
				}

				// A jammer that is not in this snapshot gets no line, like on the game's own map.
				foreach (UnitSnapshot jammedUnit in snapshot.units)
				{
					if (jammedUnit.jammedBy.HasValue && !builtIds.Contains(jammedUnit.jammedBy.Value))
					{
						jammedUnit.jammedBy = null;
					}
				}
				if (snapshot.player != null && snapshot.player.jammedBy.HasValue && !builtIds.Contains(snapshot.player.jammedBy.Value))
				{
					snapshot.player.jammedBy = null;
				}

				// Units that are gone no longer need a remembered heading.
				if (lastHeadings.Count > builtIds.Count)
				{
					var gone = new List<uint>();
					foreach (uint key in lastHeadings.Keys)
					{
						if (!builtIds.Contains(key)) gone.Add(key);
					}
					foreach (uint key in gone) lastHeadings.Remove(key);
				}

				UpdateBlasts(snapshot);

				foreach (Airbase airbase in localHq.GetAirbases())
				{
					if (airbase == null)
					{
						continue;
					}
					GlobalPosition pos = airbase.center.GlobalPosition();
					var airbaseSnap = new AirbaseSnapshot
					{
						name = airbase.SavedAirbase != null ? airbase.SavedAirbase.DisplayName : airbase.name,
						faction = FactionString(airbase.CurrentHQ),
						x = pos.x,
						z = pos.z,
						hangarsAvailable = !airbase.disabled && airbase.AnyHangarsAvailable()
					};
					if (airbase.runways != null)
					{
						foreach (Airbase.Runway runway in airbase.runways)
						{
							if (runway == null || !runway.Landing || runway.Start == null || runway.End == null)
							{
								continue;
							}
							GlobalPosition start = runway.Start.GlobalPosition();
							GlobalPosition end = runway.End.GlobalPosition();
							airbaseSnap.runways.Add(new RunwaySnapshot
							{
								startX = start.x,
								startZ = start.z,
								endX = end.x,
								endZ = end.z
							});
						}
					}
					snapshot.airbases.Add(airbaseSnap);
				}
			}

			foreach (GlobalPosition wp in DynamicMap.i.constructWaypoints)
			{
				snapshot.waypoints.Add(new WaypointSnapshot { x = wp.x, z = wp.z });
			}

			return snapshot;
		}
	}
}
