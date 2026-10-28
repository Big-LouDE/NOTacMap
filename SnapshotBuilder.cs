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
	}

	internal class PlayerSnapshot
	{
		public string type; // same unitName used for other units' classification, so the client can tell a heli apart from a plane for its own icon too
		public float speedKmh; // real Rigidbody.velocity.magnitude, see FlightHud.cs's own cockpitRB.velocity.magnitude
		public float x;
		public float y;
		public float z;
		public float heading;
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
		public List<LoadoutStation> loadout; // your own weapon stations, only while you are in an aircraft
	}

	internal static class SnapshotBuilder
	{
		private static float Round1(float value)
		{
			return Mathf.Round(value * 10f) / 10f;
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
					speedKmh = playerAircraft.rb != null ? playerAircraft.rb.velocity.magnitude * 3.6f : 0f,
					x = pos.x,
					y = pos.y,
					z = pos.z,
					heading = playerAircraft.transform.eulerAngles.y
				};

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
							selected = loadoutStation == station
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
				}
			}

			if (GameManager.GetLocalHQ(out FactionHQ localHq))
			{
				var seen = new HashSet<PersistentID>();
				var builtIds = new HashSet<uint>();

				// Missiles the local player's own RWR has already promoted from "unknown" to
				// "known" (in range and line of sight, or gated by seeker mode / network
				// share), see MissileWarning.Update(). This is the cockpit RWR's own fairness
				// gate. Scanning Missile.targetID directly would reveal a launch before the
				// player's RWR warns them.
				var incomingMissileIds = new HashSet<PersistentID>();
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
						speedKmh = unit is Missile && unit.rb != null ? Round1(unit.rb.velocity.magnitude * 3.6f) : 0f,
						x = Round1(pos.x),
						y = Round1(pos.y),
						z = Round1(pos.z),
						heading = heading
					};

					if (unit is Missile missile)
					{
						snap.isMissile = true;
						snap.isMyMissile = playerAircraft != null && missile.owner == playerAircraft;
						// Any friendly missile is exactly as visible to a teammate as any
						// other friendly unit already is via trackingDatabase/factionUnits -
						// there's no separate in-game privacy boundary to respect here, so
						// no opt-in/sharing system is needed, just widen the same detection.
						snap.isFriendlyMissile = snap.faction == "friendly";
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
