using System.Collections.Generic;
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
		public string baseType; // "aircraft" | "groundvehicle" | "building" | "ship" | "other" - a plain C# type check, never wrong, used as a fallback shape when the name isn't in the known-types list
		public string faction; // "friendly" | "enemy" | "neutral" | "spectator"
		public bool isPlayer; // human-piloted aircraft, vs. an AI/structure on the same faction
		public bool isIncomingMissile; // locked onto the local player's own aircraft right now
		public bool isMyMissile; // a missile the local player personally launched
		public bool isFriendlyMissile; // any friendly-faction missile, mine or a teammate's
		public string ownerName; // isFriendlyMissile only: pilot's real display name, null if AI-flown or unowned
		public bool hasTarget; // isFriendlyMissile only: it has a live lock
		public bool targetIsAircraft; // hasTarget only: air-to-air vs air-to-ground, for client-side filtering
		public float targetX;
		public float targetY;
		public float targetZ;
		public string targetName;
		public float speedKmh; // real Rigidbody.velocity.magnitude, not a derived estimate
		public float x;
		public float y;
		public float z;
		public float heading; // transform.eulerAngles.y - which way the icon should point on the map
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
		// Your own pre-fire target selection (Aircraft.weaponManager.GetTargetList())
		// - a plain local List<Unit> on WeaponManager, never networked, so this is
		// only ever your own; there's no way to read a teammate's or an AI's.
		public float x;
		public float y;
		public float z;
		public string name;
		public bool isPrimary; // targetList[0] - what actually fires first
	}

	internal class PlayerSnapshot
	{
		public bool inAircraft;
		public string type; // same unitName used for other units' classification, so the client can tell a heli apart from a plane for its own icon too
		public float speedKmh; // real Rigidbody.velocity.magnitude - see FlightHud.cs's own cockpitRB.velocity.magnitude
		public float x;
		public float y;
		public float z;
		public float heading;
	}

	internal class MapSnapshot
	{
		// Server-side timestamp (seconds, monotonic since game start). The
		// browser tab runs in the background on a second monitor, and
		// background tabs get their event/timer processing throttled and
		// bunched by the browser - client-side arrival time (performance.now()
		// at the moment a message happens to get processed) is not a
		// trustworthy stand-in for the real time between two snapshots. Speed
		// calculations must use deltas between two of these instead.
		public double t;
		public PlayerSnapshot player;
		public List<UnitSnapshot> units = new List<UnitSnapshot>();
		public List<AirbaseSnapshot> airbases = new List<AirbaseSnapshot>();
		public List<WaypointSnapshot> waypoints = new List<WaypointSnapshot>();
		public List<MarkedTargetSnapshot> markedTargets = new List<MarkedTargetSnapshot>();
	}

	internal static class SnapshotBuilder
	{
		// One-time data collection for a future plane/heli and tank/APC shape
		// split: the C# side only has Aircraft and GroundVehicle as base types
		// (no Helicopter/Tank/APC subclasses, no category field on either),
		// so telling them apart needs real observed unit names to build a
		// name-based classifier from - guessing now risks getting it wrong
		// (already caught "Storage Tank" being a fuel building, not armor).
		// Logs each distinct unit type exactly once per game session, with
		// enough data (C# base type, TypeIdentity, RoleIdentity) to classify
		// properly in one pass rather than needing a second data-gathering
		// session later. Read BepInEx/LogOutput.log for "NOTacMap unit-type
		// catalog" lines after a play session to build the real list.
		private static readonly HashSet<string> loggedTypeNames = new HashSet<string>();

		private static void LogUnitTypeOnce(Unit unit)
		{
			string typeKey = unit.definition != null ? unit.definition.unitName : unit.GetType().Name;
			if (!loggedTypeNames.Add(typeKey))
			{
				return;
			}
			TypeIdentity ti = unit.definition != null ? unit.definition.typeIdentity : default;
			RoleIdentity ri = unit.definition != null ? unit.definition.roleIdentity : default;
			Plugin.Log.LogInfo(
				$"NOTacMap unit-type catalog: \"{typeKey}\" | C#Type={unit.GetType().Name} | " +
				$"TypeIdentity(surface={ti.surface:0.00},air={ti.air:0.00},missile={ti.missile:0.00},radar={ti.radar:0.00},strategic={ti.strategic:0.00}) | " +
				$"RoleIdentity(antiSurface={ri.antiSurface:0.00},antiAir={ri.antiAir:0.00},antiMissile={ri.antiMissile:0.00},antiRadar={ri.antiRadar:0.00})");
		}

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

		// Returns null when there's nothing sensible to report yet
		// (main menu, hangar, loading screen, etc). Returns the plain
		// MapSnapshot object rather than a JSON string on purpose: this only
		// collects data (which needs Unity API access, so it must run on the
		// main thread), while the actual JSON serialization is pure CPU work
		// on plain data and can - and should - happen off the main thread.
		public static MapSnapshot Build()
		{
			if (DynamicMap.i == null)
			{
				return null;
			}

			var snapshot = new MapSnapshot();
			snapshot.t = Time.realtimeSinceStartupAsDouble;
			Aircraft playerAircraft = null;

			if (GameManager.GetLocalPlayer<Player>(out var localPlayer) && localPlayer.Aircraft != null && !localPlayer.Aircraft.disabled)
			{
				playerAircraft = localPlayer.Aircraft;
				GlobalPosition pos = playerAircraft.GlobalPosition();
				snapshot.player = new PlayerSnapshot
				{
					inAircraft = true,
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
						GlobalPosition targetPos = target.GlobalPosition();
						snapshot.markedTargets.Add(new MarkedTargetSnapshot
						{
							x = targetPos.x,
							y = targetPos.y,
							z = targetPos.z,
							name = target.unitName,
							isPrimary = i == 0
						});
					}
				}
			}

			if (GameManager.GetLocalHQ(out FactionHQ localHq))
			{
				var seen = new HashSet<PersistentID>();

				// Missiles the local player's own RWR has already promoted from
				// "unknown" to "known" (in range + line-of-sight, or seeker-mode/
				// network-share gated) - see MissileWarning.Update(). This is the
				// same fairness gate the real cockpit RWR uses, so we piggyback
				// on it instead of scanning Missile.targetID directly, which
				// would leak a launch before the player's RWR would ever warn them.
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

					LogUnitTypeOnce(unit);

					GlobalPosition pos;
					if (localHq.trackingDatabase.TryGetValue(id, out TrackingInfo trackingInfo))
					{
						pos = trackingInfo.GetPosition();
					}
					else
					{
						pos = unit.GlobalPosition();
					}

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
						faction = FactionString(unit.NetworkHQ),
						isPlayer = unit is Aircraft occupiedAircraft && occupiedAircraft.Player != null,
						isIncomingMissile = incomingMissileIds.Contains(id),
						speedKmh = unit.rb != null ? unit.rb.velocity.magnitude * 3.6f : 0f,
						x = pos.x,
						y = pos.y,
						z = pos.z,
						heading = unit.transform.eulerAngles.y
					};

					if (unit is Missile missile)
					{
						snap.isMyMissile = playerAircraft != null && missile.owner == playerAircraft;
						// Any friendly missile is exactly as visible to a teammate as any
						// other friendly unit already is via trackingDatabase/factionUnits -
						// there's no separate in-game privacy boundary to respect here, so
						// no opt-in/sharing system is needed, just widen the same detection.
						snap.isFriendlyMissile = snap.faction == "friendly";
						if (snap.isFriendlyMissile && missile.owner is Aircraft ownerAircraft && ownerAircraft.Player != null)
						{
							snap.ownerName = ownerAircraft.Player.GetDisplayName(PlayerNameContext.Other);
						}
						// !target.disabled matters: without it, a missile whose target was
						// destroyed by someone else while still in flight would keep
						// reporting a stale lock on a dead unit's last-known position.
						if (missile.targetID.TryGetUnit(out Unit target) && target != null && !target.disabled)
						{
							GlobalPosition targetPos = target.GlobalPosition();
							snap.hasTarget = true;
							snap.targetIsAircraft = target is Aircraft;
							snap.targetX = targetPos.x;
							snap.targetY = targetPos.y;
							snap.targetZ = targetPos.z;
							snap.targetName = target.unitName;
						}
					}

					snapshot.units.Add(snap);
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
					// unit) - add it explicitly so it doesn't go missing.
					AddUnit(id);
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
