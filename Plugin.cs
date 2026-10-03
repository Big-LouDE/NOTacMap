using System;
using System.Diagnostics;
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
		public const string PluginVersion = "1.1.0";

		internal static ManualLogSource Log;

		private ConfigEntry<int> port;
		private ConfigEntry<float> updateInterval;
		private ConfigEntry<bool> autoOpenBrowser;
		private MapServer server;
		private float timer;
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

			// MapBackground in particular is a near-saturated color (e.g. pure
			// green) at LOW alpha - in-game it's alpha-blended over the 3D
			// cockpit scene, which is what actually makes it read as dark.
			// Keep alpha and let CSS rgba() recreate that wash over our own
			// black canvas backdrop, instead of flattening it to opaque and
			// getting a neon-green screen.
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
					// UseShellExecute=true hands this to the OS exactly like
					// double-clicking the link - opens whatever the user's
					// actual default browser is, no assumption about which one.
					Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
				}
				catch (Exception ex)
				{
					// Never worth failing plugin startup over - worst case the
					// user just opens the URL themselves, same as before this
					// existed.
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

			// Skip this tick rather than queue another broadcast on top of one
			// still in flight - if the browser were ever persistently (not
			// just occasionally) slow to drain its socket, queuing regardless
			// would pile up worker threads faster than they could drain.
			// Simply dropping a stale tick's snapshot is harmless; the next
			// one 200ms later carries fresher data anyway.
			if (broadcastInFlight)
			{
				return;
			}

			var buildStopwatch = System.Diagnostics.Stopwatch.StartNew();
			MapSnapshot snapshot = SnapshotBuilder.Build();
			buildStopwatch.Stop();
			// Logged whenever it's slow enough to plausibly matter for frame
			// time, so if the game is still stalling after moving
			// serialization + the network write off the main thread, we have
			// real numbers instead of guessing a third time.
			if (buildStopwatch.ElapsedMilliseconds > 3)
			{
				Log.LogWarning($"NOTacMap: Build() took {buildStopwatch.ElapsedMilliseconds}ms on the main thread ({snapshot?.units.Count ?? 0} units, {snapshot?.airbases.Count ?? 0} airbases)");
			}

			if (snapshot != null)
			{
				broadcastInFlight = true;
				// Both the JSON serialization (previously also on the main
				// thread - plain-data CPU work, doesn't need Unity API access
				// once the snapshot object exists) and the network write
				// (genuine blocking I/O - this tab runs backgrounded on a
				// second monitor, which browsers throttle, so it can be slow
				// to drain its socket) now happen on a thread pool worker.
				// Doing either synchronously in Update() was stalling the
				// whole game (reported: 120fps -> 30-40fps while the map
				// page was open).
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
