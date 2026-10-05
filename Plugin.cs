using System;
using System.Globalization;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Newtonsoft.Json;
using NuclearOption.UIStyleSystem;
using UnityEngine;

namespace NOTacMap
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGuid = "com.bigloude.notacmap";
		public const string PluginName = "NOTacMap";
		public const string PluginVersion = "1.1.1";

		internal static ManualLogSource Log;

		private ConfigEntry<int> port;
		private ConfigEntry<float> updateInterval;
		private ConfigEntry<bool> autoOpenBrowser;
		private MapServer server;
		private float timer;
		private float keepAliveTimer;
		private float mapCheckTimer;
		private bool themeDumped;
		private volatile bool broadcastInFlight;

		private static string Hex(Color c) => ColorUtility.ToHtmlStringRGBA(c);

		internal static string ThemeJson;

		private void DumpThemeOnce()
		{
			if (themeDumped || ThemeManager.Active == null)
			{
				return;
			}
			themeDumped = true;
			ColorTheme t = ThemeManager.Active.ColorTheme;

			// MapBackground is a saturated color (e.g. pure green) at low alpha. In
			// game it is blended over the 3D cockpit scene, which is what makes it
			// look dark. Keep the alpha so CSS rgba() recreates that wash over the
			// black canvas. Flattened to opaque it would be neon green.
			string Rgba(Color c)
			{
				string hex = Hex(c); // RRGGBBAA
				int r = Convert.ToInt32(hex.Substring(0, 2), 16);
				int g = Convert.ToInt32(hex.Substring(2, 2), 16);
				int b = Convert.ToInt32(hex.Substring(4, 2), 16);
				int a = Convert.ToInt32(hex.Substring(6, 2), 16);
				// Force invariant culture: on a German (or any comma-decimal)
				// system, default ToString formatting turns "0.498" into
				// "0,498", which silently breaks the rgba(...) syntax by
				// adding a 5th comma-separated value.
				string alphaStr = (a / 255f).ToString("0.###", CultureInfo.InvariantCulture);
				return $"rgba({r},{g},{b},{alphaStr})";
			}

			ThemeJson = "{"
				+ $"\"map-bg\":\"{Rgba(t.MapBackground)}\","
				+ $"\"friendly\":\"{Rgba(t.MapIconFriendly)}\","
				+ $"\"friendly-sel\":\"{Rgba(t.MapIconFriendlySelected)}\","
				+ $"\"hostile\":\"{Rgba(t.MapIconHostile)}\","
				+ $"\"hostile-sel\":\"{Rgba(t.MapIconHostileSelected)}\","
				+ $"\"neutral\":\"{Rgba(t.MapIconNeutral)}\","
				+ $"\"target-ping\":\"{Rgba(t.TargetPing)}\","
				+ $"\"detected-ping\":\"{Rgba(t.DetectedPing)}\","
				+ $"\"passive-ping\":\"{Rgba(t.PassivePing)}\""
				+ "}";

			Log.LogInfo("NOTacMap theme captured: " + ThemeJson);
		}

		private void Awake()
		{
			Log = Logger;
			port = Config.Bind("Server", "Port", 8123,
				"Local port to serve the second-monitor map page on. Avoid 8111, used by NOX.");
			updateInterval = Config.Bind("Server", "UpdateInterval", 0.2f,
				"Seconds between snapshots pushed to connected browser tabs.");
			autoOpenBrowser = Config.Bind("Server", "AutoOpenBrowser", true,
				"Automatically open the map page in your default browser each time the game starts. Turn off if you'd rather open it yourself (e.g. you keep it pinned in a specific browser/window already).");

			server = new MapServer(port.Value);
			server.Start();
			string url = $"http://localhost:{port.Value}/";
			Log.LogInfo($"NOTacMap listening at {url} - open that on your second monitor.");

			if (autoOpenBrowser.Value)
			{
				try
				{
					// Unity's built-in call for opening the default browser, so we don't
					// start a process ourselves.
					Application.OpenURL(url);
				}
				catch (Exception ex)
				{
					// Not worth failing startup over, the user can open the URL themselves.
					Log.LogWarning($"NOTacMap: couldn't auto-open the browser ({ex.Message}). Open {url} manually.");
				}
			}
		}

		private void Update()
		{
			DumpThemeOnce();

			mapCheckTimer += Time.unscaledDeltaTime;
			if (mapCheckTimer >= 2f)
			{
				mapCheckTimer = 0f;
				MapImageCapture.CaptureIfNeeded();
			}

			timer += Time.unscaledDeltaTime;
			if (timer < updateInterval.Value)
			{
				return;
			}
			timer = 0f;

			if (!server.HasClients)
			{
				return;
			}

			// Prunes closed tabs even when there's nothing to send (main menu,
			// loading). On a worker thread for the same reason as the broadcast.
			keepAliveTimer += updateInterval.Value;
			if (keepAliveTimer >= 5f)
			{
				keepAliveTimer = 0f;
				ThreadPool.QueueUserWorkItem(_ => server.Ping());
			}

			// Skip this tick if another broadcast is still in flight. If the browser were
			// persistently slow to drain its socket, queuing anyway would pile up worker
			// threads. Dropping a stale snapshot is harmless, the next one follows a
			// moment later.
			if (broadcastInFlight)
			{
				return;
			}

			var buildStopwatch = System.Diagnostics.Stopwatch.StartNew();
			MapSnapshot snapshot = SnapshotBuilder.Build();
			buildStopwatch.Stop();
			// Logged when it is slow enough to matter for frame time.
			if (buildStopwatch.ElapsedMilliseconds > 3)
			{
				Log.LogWarning($"NOTacMap: Build() took {buildStopwatch.ElapsedMilliseconds}ms on the main thread ({snapshot?.units.Count ?? 0} units, {snapshot?.airbases.Count ?? 0} airbases)");
			}

			if (snapshot != null)
			{
				broadcastInFlight = true;
				// Serialization and the socket write don't need the Unity API, and the
				// write can block (a background browser tab drains its socket slowly),
				// so both run on a thread pool worker. Done inside Update() they would
				// stall the game.
				ThreadPool.QueueUserWorkItem(_ =>
				{
					try
					{
						string json = JsonConvert.SerializeObject(snapshot);
						server.Broadcast(json);
					}
					finally
					{
						broadcastInFlight = false;
					}
				});
			}
		}

		private void OnDestroy()
		{
			server?.Stop();
		}
	}
}
