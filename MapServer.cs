using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace NOTacMap
{
	// Serves one static page and a Server-Sent-Events stream. SSE instead of a
	// WebSocket on purpose: the feed is one-way (game -> browser) for now, and
	// HttpListener's WebSocket upgrade has had rough edges under Mono in the
	// past. Plain long-lived "text/event-stream" responses avoid that entirely
	// and every browser's EventSource API understands them natively.
	internal class MapServer
	{
		// Two listeners. The first one only takes localhost and 127.0.0.1 and is opened once, so the
		// map on this PC never restarts. The second one only takes the network address, and is
		// opened, closed and moved while the game runs when LAN access changes.
		private volatile HttpListener listener;
		private volatile HttpListener lanListener;
		private readonly object listenerLock = new object();
		private readonly Dictionary<HttpListenerResponse, string> lanClients = new Dictionary<HttpListenerResponse, string>(); // event streams that came in over the network, with the address they came from
		private readonly List<HttpListenerResponse> clients = new List<HttpListenerResponse>();
		private readonly object clientsLock = new object();
		private volatile bool running;

		private const int MaxSseClients = 8;
		private const int MaxSettingsBytes = 64 * 1024;
		private static readonly object settingsLock = new object();

		// Two tabs can save at the same moment, and a crash in the middle of a write must not leave
		// half a file. So one save at a time, written to a temp file first and then swapped in.
		private static void WriteSettingsFile(string path, string body)
		{
			lock (settingsLock)
			{
				string temp = path + ".tmp";
				File.WriteAllText(temp, body);
				try
				{
					if (File.Exists(path))
					{
						File.Replace(temp, path, null);
					}
					else
					{
						File.Move(temp, path);
					}
				}
				catch (Exception)
				{
					// Some setups don't support File.Replace. A plain copy still keeps the temp file's content whole.
					File.Copy(temp, path, true);
					try { File.Delete(temp); } catch { }
				}
			}
		}

		private static string ReadSettingsFile(string path)
		{
			lock (settingsLock)
			{
				return File.Exists(path) ? File.ReadAllText(path) : "{}";
			}
		}
		private const int MaxLanBodyBytes = 1024;
		private const int MaxSseClientsPerAddress = 3; // per device on the network, so one device can't take every slot
		private const int MaxNetworkRequests = 32;     // requests from the network being handled at once
		private int networkRequests;
		private readonly int port;
		private volatile string lanToken; // null while LAN access is off
		private volatile string lanAddress; // null when LAN access is off, or if it couldn't start
		private string refusedAddress; // an address Windows turned down, not tried again until the address changes
		private volatile RequestGuard guard;
		private readonly HashSet<string> rejectedLogged = new HashSet<string>();

		// lanToken is null to keep the map on this PC only. lanAddress can be null while
		// the PC has no network yet; SetLanAddress picks it up when one shows up.
		// EnableLan and DisableLan switch LAN access on and off while the game runs.
		public MapServer(int port, string lanAddress, string lanToken)
		{
			this.port = port;
			this.lanAddress = lanAddress;
			this.lanToken = lanToken;
			guard = new RequestGuard(port, lanAddress, lanToken);
		}

		public bool LanActive
		{
			get { return lanAddress != null; }
		}

		public string LanAddress
		{
			get { return lanAddress; }
		}

		// True while LAN access is switched on, even if no address could be used yet.
		public bool LanAllowed
		{
			get { return lanToken != null; }
		}

		// Set by the plugin. Switches LAN access on or off for the page's switch in DISPLAY.
		// Returns null when it worked, or a short message for the user when it didn't.
		public Func<bool, string> LanSwitch;

		// Set by the plugin. Makes a new secret for the phone link and cuts off the old one. Same
		// result convention as LanSwitch.
		public Func<string> LanNewLink;

		// What a phone opens. Contains the secret, so it is only ever shown on
		// this PC and never logged.
		public string LanUrl
		{
			get
			{
				string address = lanAddress;
				return address == null ? null : $"http://{address}:{port}/?t={lanToken}";
			}
		}

		private int StreamsFrom(string who)
		{
			lock (clientsLock)
			{
				return StreamsFromLocked(who);
			}
		}

		// The caller holds clientsLock.
		private int StreamsFromLocked(string who)
		{
			int n = 0;
			foreach (string other in lanClients.Values)
			{
				if (other == who) n++;
			}
			return n;
		}

		private int ClientCount
		{
			get
			{
				lock (clientsLock)
				{
					return clients.Count;
				}
			}
		}

		public bool HasClients
		{
			get
			{
				lock (clientsLock)
				{
					return clients.Count > 0;
				}
			}
		}

		public void Start()
		{
			running = true;
			lock (listenerLock)
			{
				HttpListener main = StartListener(null);
				listener = main;
				StartAccepting(main, false);
				if (lanAddress != null && lanToken != null)
				{
					OpenLanListener(lanAddress, 1);
				}
			}
		}

		private void StartAccepting(HttpListener mine, bool isLan)
		{
			var thread = new Thread(() => AcceptLoop(mine, isLan))
			{
				IsBackground = true,
				Name = isLan ? "NOTacMap-Accept-LAN" : "NOTacMap-Accept"
			};
			thread.Start();
		}

		// Opens the network listener. Returns false, with LAN access left off and the address
		// remembered as refused, when Windows won't have it (typically without admin rights).
		// The map on this PC keeps working either way. The caller holds listenerLock.
		private bool OpenLanListener(string address, int attempts)
		{
			Exception last = null;
			for (int i = 1; i <= attempts; i++)
			{
				try
				{
					HttpListener opened = StartListener(address);
					lanListener = opened;
					lanAddress = address;
					guard = new RequestGuard(port, address, lanToken);
					StartAccepting(opened, true);
					return true;
				}
				catch (Exception ex)
				{
					last = ex;
					if (i < attempts) Thread.Sleep(250);
				}
			}
			Plugin.Log?.LogWarning($"NOTacMap: couldn't listen on {address} for LAN access ({last.Message}). LAN access is off, the map still works on this PC.");
			refusedAddress = address;
			lanAddress = null;
			guard = new RequestGuard(port, null, null);
			return false;
		}

		// Only the network listener and the connections that came through it. The caller
		// holds listenerLock.
		private void CloseLanListener()
		{
			HttpListener old = lanListener;
			lanListener = null; // tells its accept loop to stop
			try { if (old != null) old.Close(); } catch { }
			DropLanClients();
			lanAddress = null;
		}

		// The event streams that came in over the network. This PC's own streams stay.
		private void DropLanClients()
		{
			List<HttpListenerResponse> gone;
			lock (clientsLock)
			{
				gone = new List<HttpListenerResponse>(lanClients.Keys);
				foreach (HttpListenerResponse client in gone)
				{
					clients.Remove(client);
					writers.Remove(client);
				}
				lanClients.Clear();
			}
			// With Abort and off this thread: a closing write into a stuck socket could wait.
			foreach (HttpListenerResponse client in gone)
			{
				AbortLater(client);
			}
		}

		// A listener for the localhost prefixes (lan is null) or for the network address only.
		private HttpListener StartListener(string lan)
		{
			var opened = new HttpListener();
			if (lan == null)
			{
				opened.Prefixes.Add($"http://localhost:{port}/");
				opened.Prefixes.Add($"http://127.0.0.1:{port}/");
			}
			else
			{
				opened.Prefixes.Add($"http://{lan}:{port}/");
			}
			try
			{
				opened.Start();
			}
			catch
			{
				try { opened.Close(); } catch { }
				throw;
			}
			return opened;
		}

		// For when the PC moves to another network or gets a new address while the game is
		// running. Only the network listener is rebuilt; the map on this PC isn't touched.
		// null turns the network listener off until an address comes back.
		public void SetLanAddress(string address)
		{
			if (lanToken == null) return;
			lock (listenerLock)
			{
				ApplyAddress(address);
			}
		}

		// Switches LAN access on with a token and an address, without a restart. If Windows
		// turns the address down, LanActive stays false and the caller says so.
		public bool EnableLan(string token, string address)
		{
			lock (listenerLock)
			{
				if (!running) return false;
				bool newToken = lanToken != null && lanToken != token;
				lanToken = token;
				refusedAddress = null;
				ApplyAddress(address);
				guard = new RequestGuard(port, lanAddress, lanToken); // a new token also needs a new guard
				if (newToken) DropLanClients(); // a phone with the old link keeps no stream open
				return true;
			}
		}

		// Back to this PC only. Drops the connections that came in over the network.
		public void DisableLan()
		{
			lock (listenerLock)
			{
				if (!running) return;
				lanToken = null;
				refusedAddress = null;
				CloseLanListener();
				guard = new RequestGuard(port, null, null);
			}
		}

		// The caller holds listenerLock.
		private void ApplyAddress(string address)
		{
			if (!running) return;
			string wanted = (address != null && address == refusedAddress) ? null : address;
			if (wanted == lanAddress) return;
			if (address != refusedAddress) refusedAddress = null;

			CloseLanListener();
			if (wanted != null && lanToken != null)
			{
				OpenLanListener(wanted, 2);
			}
			else
			{
				guard = new RequestGuard(port, null, null);
			}
		}

		public void Stop()
		{
			running = false;
			lock (listenerLock)
			{
				try { if (listener != null) listener.Stop(); } catch { }
				try { if (lanListener != null) lanListener.Stop(); } catch { }
			}
			HttpListenerResponse[] open;
			lock (clientsLock)
			{
				open = clients.ToArray();
				clients.Clear();
				lanClients.Clear();
				writers.Clear();
			}
			foreach (HttpListenerResponse client in open)
			{
				AbortLater(client);
			}
		}

		public void Broadcast(string json)
		{
			WriteToClients(Encoding.UTF8.GetBytes($"data: {json}\n\n"), true);
		}

		// An SSE comment line: browsers ignore it, but writing it finds clients
		// that have gone away. Broadcast() alone isn't enough for that, since no
		// snapshot is sent while there's no aircraft (main menu, loading).
		public void Ping()
		{
			WriteToClients(KeepAlive, false);
		}

		private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keepalive\n\n");

		// Every client has its own writer. A socket write can block for a long time (a phone that
		// went out of wifi range without closing the connection), and when all clients were
		// written one after another that froze the PC's own map too. Now a slow client only
		// holds up itself: it gets the newest snapshot when it catches up, and a client that has
		// been stuck in one write for StallLimit is dropped (the page reconnects by itself).
		private sealed class ClientWriter
		{
			public bool Busy;
			public DateTime BusySince;
			public byte[] Pending;
		}

		private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(5);
		private readonly Dictionary<HttpListenerResponse, ClientWriter> writers = new Dictionary<HttpListenerResponse, ClientWriter>();

		private void WriteToClients(byte[] payload, bool isSnapshot)
		{
			// Copy the client list under the lock, then hand out the work outside it.
			// HasClients is checked from the main thread and needs the same lock.
			HttpListenerResponse[] snapshot;
			lock (clientsLock)
			{
				snapshot = clients.ToArray();
			}

			foreach (HttpListenerResponse client in snapshot)
			{
				SendTo(client, payload, isSnapshot);
			}
		}

		private void SendTo(HttpListenerResponse client, byte[] payload, bool isSnapshot)
		{
			ClientWriter writer;
			lock (clientsLock)
			{
				if (!clients.Contains(client)) return;
				if (!writers.TryGetValue(client, out writer))
				{
					writers[client] = writer = new ClientWriter();
				}
			}

			bool stalled = false;
			lock (writer)
			{
				if (writer.Busy)
				{
					// Still sending the last one. Keep only the newest snapshot for it; a
					// keepalive is not worth waiting for.
					if (isSnapshot) writer.Pending = payload;
					stalled = DateTime.UtcNow - writer.BusySince > StallLimit;
					if (!stalled) return;
				}
				else
				{
					writer.Busy = true;
					writer.BusySince = DateTime.UtcNow;
				}
			}

			if (stalled)
			{
				Plugin.Log?.LogWarning("NOTacMap: dropped an event stream that stopped taking data (a phone out of range?). The page reconnects by itself.");
				DropClient(client, true);
				return;
			}

			ThreadPool.QueueUserWorkItem(_ => Pump(client, writer, payload));
		}

		// Writes to one client until it has caught up. Runs on a pool thread, one at a time per client.
		private void Pump(HttpListenerResponse client, ClientWriter writer, byte[] first)
		{
			byte[] next = first;
			while (next != null)
			{
				try
				{
					client.OutputStream.Write(next, 0, next.Length);
					client.OutputStream.Flush();
				}
				catch
				{
					DropClient(client, false);
					return;
				}

				lock (writer)
				{
					next = writer.Pending;
					writer.Pending = null;
					if (next == null)
					{
						writer.Busy = false;
					}
					else
					{
						writer.BusySince = DateTime.UtcNow;
					}
				}
			}
		}

		// A stuck client is cut off with Abort, because a normal Close would try to write the end of
		// the stream into the same full socket.
		private void DropClient(HttpListenerResponse client, bool abort)
		{
			lock (clientsLock)
			{
				clients.Remove(client);
				lanClients.Remove(client);
				writers.Remove(client);
			}
			if (abort)
			{
				AbortLater(client);
				return;
			}
			try { client.OutputStream.Close(); } catch { }
		}

		// Abort can wait for the write that is stuck on the same connection (seen in the game), so it
		// never runs on the thread that sends the snapshots or holds a lock.
		private static void AbortLater(HttpListenerResponse client)
		{
			ThreadPool.QueueUserWorkItem(_ =>
			{
				try { client.Abort(); } catch { }
			});
		}

		private void AcceptLoop(HttpListener mine, bool isLan)
		{
			// Ends when the server stops or when this listener has been replaced or closed.
			while (running && ReferenceEquals(mine, isLan ? lanListener : listener))
			{
				try
				{
					HttpListenerContext context = mine.GetContext();
					ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
				}
				catch (HttpListenerException)
				{
					break; // listener was stopped
				}
				catch (ObjectDisposedException)
				{
					break;
				}
				catch (Exception ex)
				{
					if (!ReferenceEquals(mine, isLan ? lanListener : listener)) break;
					Plugin.Log?.LogWarning($"NOTacMap accept loop error: {ex.Message}");
				}
			}
		}

		private void HandleRequest(HttpListenerContext context)
		{
			IPAddress sender = null;
			bool fromNetwork = false;
			bool counted = false; // this request holds one of the network request slots
			try
			{
				sender = context.Request.RemoteEndPoint?.Address;
				fromNetwork = sender != null && !IPAddress.IsLoopback(sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender);
				if (fromNetwork)
				{
					if (Interlocked.Increment(ref networkRequests) > MaxNetworkRequests)
					{
						Interlocked.Decrement(ref networkRequests);
						Reject(context, 503);
						return;
					}
					counted = true;
				}

				context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
				context.Response.Headers.Add("X-Frame-Options", "DENY");
				context.Response.Headers.Add("Referrer-Policy", "no-referrer");

				HttpListenerRequest request = context.Request;
				string path = request.Url.AbsolutePath;
				string method = request.HttpMethod;

				RequestGuard usedGuard = guard;
				GuardDecision decision = usedGuard.Evaluate(request.Headers["Host"], request.Headers["Origin"],
					request.RemoteEndPoint?.Address, method, path, request.Url.Query, request.Headers["Cookie"],
					request.Headers["Sec-Fetch-Site"]);
				if (decision.Status == 302)
				{
					context.Response.AddHeader("Set-Cookie", decision.SetCookie);
					context.Response.StatusCode = 302;
					context.Response.RedirectLocation = decision.RedirectTo;
					context.Response.Close();
					return;
				}
				if (decision.Status != 0)
				{
					LogRejected(request, decision);
					Reject(context, decision.Status, decision.Message);
					return;
				}

				if (path == "/events")
				{
					// Closed tabs only drop out of the list when a write to them
					// fails, and the first write to a dead socket can still
					// succeed. Ping twice before turning anyone away, so a few
					// reloads in a row never fill the list with ghosts.
					string who = fromNetwork ? (sender.IsIPv4MappedToIPv6 ? sender.MapToIPv4() : sender).ToString() : null;
					if (ClientCount >= MaxSseClients || (who != null && StreamsFrom(who) >= MaxSseClientsPerAddress))
					{
						Ping();
						Thread.Sleep(150);
						Ping();
					}

					HttpListenerResponse response = context.Response;
					response.ContentType = "text/event-stream";
					response.Headers.Add("Cache-Control", "no-cache");
					response.SendChunked = true;
					lock (clientsLock)
					{
						if (clients.Count >= MaxSseClients)
						{
							Reject(context, 503);
							return;
						}
						if (fromNetwork)
						{
							// The secret or the address changed while this request was on its way in
							// (a new link, a moved listener): it was let in by a guard that is gone.
							if (!ReferenceEquals(usedGuard, guard) || StreamsFromLocked(who) >= MaxSseClientsPerAddress)
							{
								Reject(context, 503);
								return;
							}
							lanClients[response] = who;
						}
						clients.Add(response);
					}

					// Headers only go out with the first write. Sending one
					// comment now lets the page see the connection open at
					// once instead of waiting for the next snapshot or ping.
					SendTo(response, KeepAlive, false);
					// Response is left open; Broadcast()/Stop() write to and close it.
					return;
				}

				if (path == "/" || path == "/index.html")
				{
					ServeFile(context, "index.html", "text/html");
					return;
				}

				if (path == "/about")
				{
					string aboutJson = Newtonsoft.Json.JsonConvert.SerializeObject(new
					{
						version = Plugin.PluginVersion,
						label = Plugin.BuildLabel
					});
					byte[] aboutBytes = Encoding.UTF8.GetBytes(aboutJson);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = aboutBytes.Length;
					context.Response.OutputStream.Write(aboutBytes, 0, aboutBytes.Length);
					context.Response.Close();
					return;
				}

				if (path == "/theme")
				{
					if (Plugin.ThemeJson == null)
					{
						context.Response.StatusCode = 404;
						context.Response.Close();
						return;
					}
					byte[] bytes = Encoding.UTF8.GetBytes(Plugin.ThemeJson);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = bytes.Length;
					context.Response.OutputStream.Write(bytes, 0, bytes.Length);
					context.Response.Close();
					return;
				}

				if (path == "/mapimage.png")
				{
					if (MapImageCapture.Png == null)
					{
						context.Response.StatusCode = 404;
						context.Response.Close();
						return;
					}
					context.Response.ContentType = "image/png";
					context.Response.ContentLength64 = MapImageCapture.Png.Length;
					context.Response.OutputStream.Write(MapImageCapture.Png, 0, MapImageCapture.Png.Length);
					context.Response.Close();
					return;
				}

				if (path == "/mapwater.png")
				{
					if (MapImageCapture.WaterPng == null)
					{
						context.Response.StatusCode = 404;
						context.Response.Close();
						return;
					}
					context.Response.ContentType = "image/png";
					context.Response.ContentLength64 = MapImageCapture.WaterPng.Length;
					context.Response.OutputStream.Write(MapImageCapture.WaterPng, 0, MapImageCapture.WaterPng.Length);
					context.Response.Close();
					return;
				}

				if (path == "/mapinfo")
				{
					if (MapImageCapture.Png == null)
					{
						context.Response.StatusCode = 404;
						context.Response.Close();
						return;
					}
					// Built by the serializer, so a map name with a quote or a backslash can't break the JSON.
					string json = Newtonsoft.Json.JsonConvert.SerializeObject(new
					{
						mapSizeX = MapImageCapture.MapSizeX,
						mapSizeY = MapImageCapture.MapSizeY,
						offsetX = MapImageCapture.OffsetX,
						offsetY = MapImageCapture.OffsetY,
						mapName = MapImageCapture.MapName
					});
					byte[] bytes = Encoding.UTF8.GetBytes(json);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = bytes.Length;
					context.Response.OutputStream.Write(bytes, 0, bytes.Length);
					context.Response.Close();
					return;
				}

				if (path == "/lan")
				{
					string error = null;
					if (context.Request.HttpMethod == "POST")
					{
						// Same rules as saving settings: our own page sends application/json,
						// which a cross-site form or no-cors request can't.
						string contentType = context.Request.ContentType ?? "";
						if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
						{
							Reject(context, 415);
							return;
						}
						if (context.Request.ContentLength64 > MaxLanBodyBytes)
						{
							Reject(context, 413);
							return;
						}
						byte[] buffer = new byte[MaxLanBodyBytes + 1];
						int total = 0;
						int read;
						Stream input = context.Request.InputStream;
						while (total < buffer.Length && (read = input.Read(buffer, total, buffer.Length - total)) > 0)
						{
							total += read;
						}
						if (total > MaxLanBodyBytes)
						{
							Reject(context, 413);
							return;
						}

						bool turnOn = false;
						bool newLink = false;
						try
						{
							var body = Newtonsoft.Json.Linq.JObject.Parse(Encoding.UTF8.GetString(buffer, 0, total));
							var flag = body["enabled"];
							var fresh = body["newLink"];
							bool hasFlag = flag != null;
							bool hasFresh = fresh != null;
							// Exactly one of the two, and it has to be a real boolean.
							if (hasFlag == hasFresh
								|| (hasFlag && flag.Type != Newtonsoft.Json.Linq.JTokenType.Boolean)
								|| (hasFresh && (fresh.Type != Newtonsoft.Json.Linq.JTokenType.Boolean || !(bool)fresh)))
							{
								Reject(context, 400);
								return;
							}
							if (hasFlag) turnOn = (bool)flag; else newLink = true;
						}
						catch (Newtonsoft.Json.JsonException)
						{
							Reject(context, 400);
							return;
						}
						if (newLink)
						{
							Func<string> makeLink = LanNewLink;
							error = makeLink == null ? "A new link can't be made from here." : makeLink();
						}
						else
						{
							Func<bool, string> handler = LanSwitch;
							error = handler == null ? "LAN access can't be switched from here." : handler(turnOn);
						}
					}

					string lanUrl = LanUrl;
					string json = lanUrl == null
						? "{\"enabled\":false"
						: "{\"enabled\":true,\"url\":\"" + lanUrl + "\"";
					if (error != null) json += ",\"error\":" + Newtonsoft.Json.JsonConvert.ToString(error);
					json += "}";
					byte[] bytes = Encoding.UTF8.GetBytes(json);
					context.Response.ContentType = "application/json";
					context.Response.Headers.Add("Cache-Control", "no-store");
					context.Response.ContentLength64 = bytes.Length;
					context.Response.OutputStream.Write(bytes, 0, bytes.Length);
					context.Response.Close();
					return;
				}

				if (path == "/settings")
				{
					// Opaque pass-through storage: the server keeps whatever JSON the client sends
					// and returns it later, so the settings schema stays in the page and a new
					// checkbox in index.html needs no C# change.
					string settingsPath = Path.Combine(Path.GetDirectoryName(typeof(MapServer).Assembly.Location), "settings.json");
					if (context.Request.HttpMethod == "POST")
					{
						// Our own page always sends application/json. Requiring it
						// means a cross-site form or no-cors request can't write
						// here, since a browser won't send that type without a
						// preflight we never answer.
						string contentType = context.Request.ContentType ?? "";
						if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
						{
							Reject(context, 415);
							return;
						}
						if (context.Request.ContentLength64 > MaxSettingsBytes)
						{
							Reject(context, 413);
							return;
						}

						// Read at most one byte past the limit, so a chunked body
						// with no declared length can't grow without bound.
						byte[] buffer = new byte[MaxSettingsBytes + 1];
						int total = 0;
						int read;
						Stream input = context.Request.InputStream;
						while (total < buffer.Length && (read = input.Read(buffer, total, buffer.Length - total)) > 0)
						{
							total += read;
						}
						if (total > MaxSettingsBytes)
						{
							Reject(context, 413);
							return;
						}

						string body = Encoding.UTF8.GetString(buffer, 0, total);
						try
						{
							// Still stored as an opaque blob, we only check that
							// it is a JSON object before writing it to disk.
							Newtonsoft.Json.Linq.JObject.Parse(body);
						}
						catch (Newtonsoft.Json.JsonException)
						{
							Reject(context, 400);
							return;
						}
						WriteSettingsFile(settingsPath, body);
						context.Response.StatusCode = 204;
						context.Response.Close();
						return;
					}

					string json = ReadSettingsFile(settingsPath);
					byte[] bytes = Encoding.UTF8.GetBytes(json);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = bytes.Length;
					context.Response.OutputStream.Write(bytes, 0, bytes.Length);
					context.Response.Close();
					return;
				}

				context.Response.StatusCode = 404;
				context.Response.Close();
			}
			catch (Exception ex)
			{
				Plugin.Log?.LogWarning($"NOTacMap request error: {ex.Message}");
				try { context.Response.Close(); } catch { }
			}
			finally
			{
				if (counted) Interlocked.Decrement(ref networkRequests);
			}
		}

		private static void Reject(HttpListenerContext context, int statusCode, string message = null)
		{
			try
			{
				context.Response.StatusCode = statusCode;
				if (message != null)
				{
					byte[] bytes = Encoding.UTF8.GetBytes(message);
					context.Response.ContentType = "text/plain; charset=utf-8";
					context.Response.ContentLength64 = bytes.Length;
					context.Response.OutputStream.Write(bytes, 0, bytes.Length);
				}
				context.Response.Close();
			}
			catch { }
		}

		// Says once per device and reason when something on the network is turned
		// away, so a phone that opened the wrong link shows up in the log. Requests from this
		// PC are not logged.
		private void LogRejected(HttpListenerRequest request, GuardDecision decision)
		{
			IPAddress from = request.RemoteEndPoint?.Address;
			if (from == null || IPAddress.IsLoopback(from)) return;
			string key = from + "|" + decision.Reason;
			lock (rejectedLogged)
			{
				if (rejectedLogged.Count >= 32 || !rejectedLogged.Add(key)) return;
			}
			Plugin.Log?.LogInfo($"NOTacMap: turned away a request from {from} (status {decision.Status}: {decision.Reason}).");
		}

		private static void ServeFile(HttpListenerContext context, string fileName, string contentType)
		{
			string path = Path.Combine(Path.GetDirectoryName(typeof(MapServer).Assembly.Location), fileName);
			byte[] bytes = File.ReadAllBytes(path);
			context.Response.ContentType = contentType;
			context.Response.ContentLength64 = bytes.Length;
			context.Response.OutputStream.Write(bytes, 0, bytes.Length);
			context.Response.Close();
		}
	}
}
