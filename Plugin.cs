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
	// Read by name by the BepInEx Configuration Manager (F1), which looks for fields with these names.
	internal sealed class ConfigurationManagerAttributes
	{
		public bool? Browsable;
		public string DispName;
		public int? Order;
		public bool? HideDefaultButton;
		public System.Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawer;
	}

	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGuid = "com.bigloude.notacmap";
		public const string PluginName = "NOTacMap";
		public const string PluginVersion = "1.3.0";
		// Shown in the log and in Help. "dev" while building, "beta1", "beta2" for the test releases, empty for the real 1.3.0.
		// It is not part of PluginVersion: BepInEx can only read a plain number there.
		internal const string BuildLabel = "";
		// The Nuclear Option version this was last checked with. A game update can break what the map reads until NOTacMap follows.
		internal const string TestedGameVersion = "0.34.6";
		// Read once at startup (Application.version can only be used on the main thread, the web server thread reads this copy).
		internal static string GameVersion = "";

		internal static ManualLogSource Log;

		private ConfigEntry<int> port;
		private ConfigEntry<float> updateInterval;
		private ConfigEntry<bool> autoOpenBrowser;
		private ConfigEntry<bool> allowLan;
		private ConfigEntry<bool> httpsEnabled;
		private ConfigEntry<int> httpsPort;
		private ConfigEntry<bool> httpsOnly;
		private ConfigEntry<string> lanAddress;
		private ConfigEntry<string> lanToken;
		// The map's keys and controller buttons (DISPLAY > Keys). Their press counts go out with every snapshot and the page acts
		// on the difference, so a press is not lost when a snapshot is skipped, and every open map sees the same presses.
		private MapControls controls;
		private MapServer server;
		private Timer networkWatch;
		private string pendingLan = NoPendingLan;
		private const string NoPendingLan = "";
		private float timer;
		private float keepAliveTimer;
		private float mapCheckTimer;
		private bool themeDumped;
		private volatile bool broadcastInFlight;
		private float lastSlowBuildLog = -100f;

		// One snapshot goes out at a time (broadcastInFlight), so the serializer and its buffer are reused. The text is written
		// straight into the buffer as UTF-8, with the event stream framing around it. Going through a json string, an
		// interpolated string and then bytes made three copies of a payload that is large on a busy server (and those copies
		// landed in the large object heap, which is only cleaned by full collections).
		private static readonly JsonSerializer snapshotSerializer = new JsonSerializer();
		private static readonly System.IO.MemoryStream snapshotBuffer = new System.IO.MemoryStream(1 << 16);
		private static System.IO.StreamWriter snapshotText;
		private static JsonTextWriter snapshotJson;
		private static readonly byte[] eventHead = System.Text.Encoding.ASCII.GetBytes("data: ");
		private static readonly byte[] eventTail = System.Text.Encoding.ASCII.GetBytes("\n\n");

		private static byte[] SerializeEvent(MapSnapshot snapshot)
		{
			try
			{
				if (snapshotJson == null)
				{
					snapshotText = new System.IO.StreamWriter(snapshotBuffer, new System.Text.UTF8Encoding(false), 1 << 12);
					snapshotJson = new JsonTextWriter(snapshotText) { CloseOutput = false };
				}
				snapshotBuffer.SetLength(0);
				snapshotBuffer.Write(eventHead, 0, eventHead.Length);
				snapshotSerializer.Serialize(snapshotJson, snapshot);
				snapshotJson.Flush();
				snapshotBuffer.Write(eventTail, 0, eventTail.Length);
				return snapshotBuffer.ToArray();
			}
			catch
			{
				// A writer that stopped halfway is not used again: the next snapshot starts a new one.
				snapshotJson = null;
				throw;
			}
		}

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
			port = Config.Bind("Server", "Port", 8123, new ConfigDescription(
				"The port the map page is served on, so the map is at http://127.0.0.1:<port>/. Change it only if something else already uses it (the NOX mod uses 8111). Takes effect the next time the game starts.",
				new AcceptableValueRange<int>(1024, 65535)));
			updateInterval = Config.Bind("Server", "UpdateInterval", 0.2f, new ConfigDescription(
				"How often, in seconds, the game sends a new picture of the map to the open page. Lower is smoother and costs a little more. The default of 0.2 is 5 times a second.",
				new AcceptableValueRange<float>(0.05f, 5f)));
			autoOpenBrowser = Config.Bind("Server", "AutoOpenBrowser", true,
				"Open the map page in your default browser each time the game starts. Turn it off if you would rather open the page yourself, for example in a browser window you keep pinned on the second monitor.");

			allowLan = Config.Bind("Server", "AllowLan", false,
				"Let other devices on your home network (a phone or tablet) open the map. Off by default. The map page switches this for you (the DEVICES button, or the question when the page opens) and keeps this value in step. If you set it here yourself, it takes effect the next time the game starts. Anyone on your network who has the link can see the map, so only turn it on on a network you trust.");
			lanAddress = Config.Bind("Server", "LanAddress", "",
				"The network address of this PC that other devices should use. Leave it empty and the plugin finds your private address (192.168.x.x, 10.x.x.x, 172.16 to 31.x.x or 169.254.x.x) and follows it if it changes. Only set it if you are on several networks and the wrong one is picked.");
			// Browsable = false keeps the secret out of the F1 Configuration Manager window, where it
			// would show on screen or on a stream. It stays in the config file.
			lanToken = Config.Bind("Server", "LanToken", "", new ConfigDescription(
				"The secret in the phone link. It is made automatically the first time you turn on access from other devices. The map page has a New link button that makes a new one and cuts off the old links. You can also delete this value and restart the game. If you write one yourself, use letters, digits, - and _ only, at least 16 of them.",
				null,
				new ConfigurationManagerAttributes { Browsable = false }));

			httpsEnabled = Config.Bind("Server", "HttpsEnabled", false,
				"EXPERIMENTAL. Offer the phone link over https, so the phone's browser can keep the screen on while the map is open (browsers only allow that on a secure page). Needs access from other devices. The browser warns that it cannot check the certificate, because the plugin makes it itself and not a company. The link is still encrypted. Only tap through on a link you got from your own PC. The map page switches this for you (DEVICES) and keeps this value in step.");
			httpsPort = Config.Bind("Server", "HttpsPort", 8124, new ConfigDescription(
				"The port of the https phone link, next to the normal port. Change it only if something else uses it. Takes effect the next time the game starts or HTTPS is switched.",
				new AcceptableValueRange<int>(1024, 65535)));

			httpsOnly = Config.Bind("Server", "HttpsOnly", false,
				"EXPERIMENTAL, only means something while HTTPS runs. When on, other devices can only use the https link and the plain http link answers them with a refusal. This PC is not affected. Off by default. Takes effect the next time the game starts or the network address changes.");

			// No key or button is set at first, so nothing clashes with the game's own bindings.
			controls = new MapControls(Config);

			string lan = null;
			string token = null;
			// With no address in the config, the plugin follows the PC's own address, so a new
			// wifi or a new lease while the game runs doesn't leave the phone link stale.
			bool followNetwork = string.IsNullOrWhiteSpace(lanAddress.Value);
			if (allowLan.Value)
			{
				string problem;
				lan = LanAddress.Resolve(lanAddress.Value, out problem);
				if (lan == null && !followNetwork)
				{
					Log.LogWarning($"NOTacMap: LAN access is on in the config but can't be used: {problem}. The map is only available on this PC.");
				}
				else
				{
					if (lan == null)
					{
						Log.LogWarning($"NOTacMap: LAN access is on in the config but there is no network address yet ({problem}). The map is only available on this PC until there is one.");
					}
					if (!RequestGuard.IsValidToken(lanToken.Value))
					{
						if (!string.IsNullOrEmpty(lanToken.Value))
						{
							Log.LogWarning("NOTacMap: LanToken in the config is too short or has characters that don't belong in a link. A new one was created, so old links stop working.");
						}
						lanToken.Value = NewToken();
						Config.Save();
					}
					token = lanToken.Value;
				}
			}

			server = new MapServer(port.Value, lan, token);
			server.LanSwitch = SwitchLan;
			server.ControlsState = controls.StateJson;
			server.ControlsCommand = controls.Command;
			server.LanNewLink = NewLink;
			server.HttpsSwitch = SwitchHttps;
			server.HttpsFrontStart = StartHttpsFront;
			server.HttpsReached = HttpsReached;
			server.ConfigureHttps(httpsEnabled.Value && httpsPort.Value != port.Value, httpsPort.Value, httpsOnly.Value);
			try
			{
				server.Start();
			}
			catch (Exception ex)
			{
				Log.LogError($"NOTacMap: couldn't start the map server on port {port.Value} ({ex.Message.Trim()}). Something else probably uses that port (the NOX mod uses 8111). Set Server > Port in {Config.ConfigFilePath} to a free port and restart the game. The map stays off until then, the game itself is not affected.");
				try { server.Stop(); } catch { /* it never fully started */ }
				server = null;
				return;
			}
			// 127.0.0.1 and not "localhost": the game's web server knows the name "localhost" on one
			// loopback address only (usually the IPv6 one), so a browser that reaches localhost over
			// IPv4, Firefox for one, got "Bad Request (Invalid host)". The IPv4 address always works.
			string url = $"http://127.0.0.1:{port.Value}/";
			Log.LogInfo($"NOTacMap listening at {url}, open that on your second monitor.");
			if (BuildLabel.Length > 0)
			{
				Log.LogInfo($"NOTacMap {PluginVersion} {BuildLabel}: this is a test version, not a normal release.");
			}
			GameVersion = Application.version ?? "";
			if (GameVersion.Length > 0 && GameVersion != TestedGameVersion)
			{
				Log.LogInfo($"NOTacMap was last checked with Nuclear Option {TestedGameVersion}. You have {GameVersion}. If the map acts up after a game update, look for a newer NOTacMap.");
			}
			if (server.LanActive)
			{
				// The link itself holds the secret, so it isn't logged. The page
				// shows it under DEVICES, on this PC only.
				Log.LogInfo($"NOTacMap LAN access is on. Other devices on this network can open the map with the link on the map page (DEVICES button).");
			}

			if (followNetwork)
			{
				networkWatch = new Timer(_ => CheckNetwork(), null, NetworkCheckMs, NetworkCheckMs);
			}

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

		private const int NetworkCheckMs = 10000;

		// Runs on a worker thread every few seconds. If the PC's address is no longer the one
		// the server listens on, the server moves to the new one. The same answer has to come
		// twice in a row first, so a wifi blip doesn't restart the listener.
		private void CheckNetwork()
		{
			try
			{
				if (server == null || !server.LanAllowed) return;
				string problem;
				string found = LanAddress.Resolve("", out problem);
				if (found == server.LanAddress)
				{
					pendingLan = NoPendingLan;
					return;
				}
				if (found != pendingLan)
				{
					pendingLan = found;
					return;
				}
				pendingLan = NoPendingLan;
				server.SetLanAddress(found);
				if (server.LanActive)
				{
					Log.LogInfo("NOTacMap: this PC's network address changed. LAN access moved to the new one, the link on the map page is updated.");
				}
				else if (found == null)
				{
					Log.LogInfo("NOTacMap: no network address found. LAN access is paused until there is one.");
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning($"NOTacMap: checking the network address failed ({ex.Message}).");
			}
		}

		private readonly object lanSwitchLock = new object();

		// The switch in the page's DISPLAY panel (this PC only). Turns LAN access on or off
		// while the game runs and keeps the config in step, so the choice survives a restart.
		// Returns null when it worked, else a message the page shows to the user.
		private string SwitchLan(bool on)
		{
			lock (lanSwitchLock)
			{
				if (!on)
				{
					server.DisableLan();
					allowLan.Value = false;
					Log.LogInfo("NOTacMap: LAN access turned off from the map page.");
					return null;
				}

				string problem;
				string address = LanAddress.Resolve(lanAddress.Value, out problem);
				if (address == null)
				{
					return string.IsNullOrWhiteSpace(lanAddress.Value)
						? "No private network address found on this PC. Connect to your wifi or router and try again."
						: "The LanAddress in the config is not a private network address.";
				}
				if (!RequestGuard.IsValidToken(lanToken.Value))
				{
					lanToken.Value = NewToken();
					Config.Save();
				}
				if (!server.EnableLan(lanToken.Value, address))
				{
					return "The game is closing, so the map can't be shared right now.";
				}
				if (!server.LanActive)
				{
					server.DisableLan();
					return "Windows would not let the game listen on the network, so the map can't be shared.";
				}
				allowLan.Value = true;
				Log.LogInfo("NOTacMap: LAN access turned on from the map page. Other devices on this network can open the map with the link shown there.");
				return null;
			}
		}

		// Makes (or loads) the certificate for this address and starts the TLS front. Runs on a worker thread, never under the server's lock.
		private IDisposable StartHttpsFront(string address, int frontPort, int backendPort)
		{
			// The certificate is kept in BepInEx's cache folder. Older builds put it next to the plugin, that copy is removed.
			TlsCertificate.RemoveOldFiles(System.IO.Path.GetDirectoryName(typeof(MapServer).Assembly.Location));
			System.Security.Cryptography.X509Certificates.X509Certificate2 certificate = TlsCertificate.LoadOrCreate(BepInEx.Paths.CachePath, address);
			IDisposable started = TlsFront.Start(address, frontPort, backendPort, certificate);
			string fingerprint = TlsCertificate.Fingerprint(certificate);
			server.HttpsFingerprint = fingerprint;
			Log.LogInfo($"NOTacMap: HTTPS (experimental) is on at https://{address}:{frontPort}/ . The phone's browser will warn that it cannot check the certificate, because the plugin makes it itself. The link is still encrypted. Only tap through on a link you got from your own PC. Certificate SHA-256: {fingerprint}");
			return started;
		}

		// The state asked for is there: the config follows, so what is saved is what is really running.
		private void HttpsReached(bool running)
		{
			lock (lanSwitchLock)
			{
				try
				{
					if (httpsEnabled.Value != running)
					{
						httpsEnabled.Value = running;
						Config.Save();
					}
				}
				catch (Exception ex)
				{
					Log.LogWarning($"NOTacMap: couldn't save the HTTPS choice to the config ({ex.Message}).");
				}
			}
			Log.LogInfo(running ? "NOTacMap: HTTPS is running." : "NOTacMap: HTTPS is off.");
		}

		// The HTTPS switch in the page's DEVICES panel (this PC only). Turning it on only starts it, the page asks /lan until it is up.
		private string SwitchHttps(bool on)
		{
			if (on)
			{
				if (!server.LanActive)
				{
					return "Turn on access from other devices first.";
				}
				if (httpsPort.Value == port.Value)
				{
					return "HttpsPort in the config is the same as Port. Give it another one.";
				}
			}
			server.SetHttps(on);
			return null;
		}

		// The "New link" button in the page (this PC only). Makes a new secret, saves it, and cuts
		// off every device that has the old link. Returns null when it worked, else a message.
		private string NewLink()
		{
			lock (lanSwitchLock)
			{
				if (!server.LanActive)
				{
					return "Turn on access from other devices first.";
				}
				string token = NewToken();
				string oldToken = lanToken.Value;
				// Saved first, so a restart can never bring the old link back.
				try
				{
					lanToken.Value = token;
					Config.Save();
				}
				catch (Exception ex)
				{
					Log.LogWarning($"NOTacMap: couldn't save the new link to the config ({ex.Message}), so nothing was changed.");
					try { lanToken.Value = oldToken; } catch { /* the old value is still what the server uses */ }
					return "The new link couldn't be saved to the config, so nothing was changed.";
				}
				if (!server.EnableLan(token, server.LanAddress))
				{
					return "The game is closing, so a new link can't be made right now.";
				}
				Log.LogInfo("NOTacMap: a new phone link was made from the map page. The old links stop working.");
				return null;
			}
		}

		// 128 random bits as hex.
		private static string NewToken()
		{
			var bytes = new byte[16];
			using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
			{
				rng.GetBytes(bytes);
			}
			var sb = new System.Text.StringBuilder(32);
			foreach (byte b in bytes) sb.Append(b.ToString("x2"));
			return sb.ToString();
		}

		private void Update()
		{
			if (server == null)
			{
				return;
			}

			DumpThemeOnce();
			RadarCheck.TryOnce();
			RadarCheck.Watch();
			OwnRadar.Update();
			ReadKeys();

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
			MapSnapshot snapshot = null;
			try
			{
				snapshot = SnapshotBuilder.Build();
			}
			catch (Exception ex)
			{
				// After a game update one internal call can start failing. Update runs five times a
				// second here, so each distinct error is logged once and then stays quiet.
				LogOnce("building the map", ex);
			}
			buildStopwatch.Stop();
			// Logged when it is slow enough to matter for frame time: a real hitch, and not more than once every 15 seconds (it runs five
			// times a second, so a busy server at 4 or 5 ms every time would fill the log).
			if (buildStopwatch.ElapsedMilliseconds > 8 && Time.realtimeSinceStartup - lastSlowBuildLog > 15f)
			{
				lastSlowBuildLog = Time.realtimeSinceStartup;
				Log.LogWarning($"NOTacMap: Build() took {buildStopwatch.ElapsedMilliseconds}ms on the main thread ({snapshot?.units.Count ?? 0} units, {snapshot?.airbases.Count ?? 0} airbases)");
			}

			if (snapshot != null)
			{
				snapshot.keys = controls.Presses();
				broadcastInFlight = true;
				// Serialization and the socket write don't need the Unity API, and the
				// write can block (a background browser tab drains its socket slowly),
				// so both run on a thread pool worker. Done inside Update() they would
				// stall the game.
				ThreadPool.QueueUserWorkItem(_ =>
				{
					try
					{
						server.BroadcastEvent(SerializeEvent(snapshot));
					}
					catch (Exception ex)
					{
						LogOnce("sending the map", ex);
					}
					finally
					{
						broadcastInFlight = false;
					}
				});
			}
		}

		// Runs every frame, so a short press is never missed between two snapshots.
		private void ReadKeys()
		{
			try
			{
				controls.Update(TypingInTextField());
			}
			catch (Exception ex)
			{
				LogOnce("reading the map keys", ex);
			}
		}

		// The same test the game uses before its own keys do anything (InputFieldChecker): a focused text field, like the chat,
		// takes the keys.
		private static bool TypingInTextField()
		{
			var system = UnityEngine.EventSystems.EventSystem.current;
			GameObject selected = system != null ? system.currentSelectedGameObject : null;
			if (selected == null)
			{
				return false;
			}
			if (selected.TryGetComponent<TMPro.TMP_InputField>(out var tmp))
			{
				return tmp.isFocused;
			}
			if (selected.TryGetComponent<UnityEngine.UI.InputField>(out var field))
			{
				return field.isFocused;
			}
			return false;
		}

		private readonly System.Collections.Generic.HashSet<string> loggedErrors = new System.Collections.Generic.HashSet<string>();

		// Logs an error the first time it is seen and then not again, so a failure that repeats on
		// every update doesn't fill the log. Capped, in case the message keeps changing.
		private void LogOnce(string what, Exception ex)
		{
			string key = what + "|" + ex.GetType().Name + "|" + ex.Message;
			lock (loggedErrors)
			{
				if (loggedErrors.Count >= 20 || !loggedErrors.Add(key))
				{
					return;
				}
			}
			Log.LogError($"NOTacMap: a problem while {what}: {ex}. This is logged once, the map keeps trying.");
		}

		private void OnDestroy()
		{
			networkWatch?.Dispose();
			server?.Stop();
		}
	}
}
