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
		private HttpListener listener;
		private readonly List<HttpListenerResponse> clients = new List<HttpListenerResponse>();
		private readonly object clientsLock = new object();
		private readonly object writeLock = new object();
		private Thread acceptThread;
		private volatile bool running;

		private const int MaxSseClients = 8;
		private const int MaxSettingsBytes = 64 * 1024;
		private readonly int port;
		private readonly string lanToken;
		private string lanAddress; // null when LAN access is off, or if it couldn't start
		private volatile RequestGuard guard;
		private readonly HashSet<string> rejectedLogged = new HashSet<string>();

		// lanAddress and lanToken are both null to keep the map on this PC only.
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

		// What a phone opens. Contains the secret, so it is only ever shown on
		// this PC and never logged.
		public string LanUrl
		{
			get { return lanAddress == null ? null : $"http://{lanAddress}:{port}/?t={lanToken}"; }
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
			try
			{
				StartListener(lanAddress);
			}
			catch (Exception ex) when (lanAddress != null)
			{
				// Typically Windows refusing a listener on a network address
				// without admin rights. The map must still work on this PC.
				Plugin.Log?.LogWarning($"NOTacMap: couldn't listen on {lanAddress} for LAN access ({ex.Message}). LAN access is off, the map still works on this PC.");
				try { listener.Close(); } catch { }
				lanAddress = null;
				guard = new RequestGuard(port, null, null);
				StartListener(null);
			}
			acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "NOTacMap-Accept" };
			acceptThread.Start();
		}

		private void StartListener(string lan)
		{
			listener = new HttpListener();
			listener.Prefixes.Add($"http://localhost:{port}/");
			listener.Prefixes.Add($"http://127.0.0.1:{port}/");
			if (lan != null)
			{
				listener.Prefixes.Add($"http://{lan}:{port}/");
			}
			listener.Start();
		}

		public void Stop()
		{
			running = false;
			try { listener.Stop(); } catch { }
			lock (clientsLock)
			{
				foreach (HttpListenerResponse client in clients)
				{
					try { client.OutputStream.Close(); } catch { }
				}
				clients.Clear();
			}
		}

		public void Broadcast(string json)
		{
			WriteToClients(Encoding.UTF8.GetBytes($"data: {json}\n\n"));
		}

		// An SSE comment line: browsers ignore it, but writing it finds clients
		// that have gone away. Broadcast() alone isn't enough for that, since no
		// snapshot is sent while there's no aircraft (main menu, loading).
		public void Ping()
		{
			WriteToClients(KeepAlive);
		}

		private static readonly byte[] KeepAlive = Encoding.UTF8.GetBytes(": keepalive\n\n");

		private void WriteToClients(byte[] payload)
		{
			// Copy the client list under the lock, then write outside it.
			// HasClients is checked from the main thread and needs the same lock, so
			// a slow socket write inside it would stall the game.
			HttpListenerResponse[] snapshot;
			lock (clientsLock)
			{
				snapshot = clients.ToArray();
			}

			// One writer at a time: a snapshot and a keepalive ping arriving on
			// different threads would otherwise interleave bytes on the stream.
			List<HttpListenerResponse> failed = null;
			lock (writeLock)
			{
				foreach (HttpListenerResponse client in snapshot)
				{
					try
					{
						client.OutputStream.Write(payload, 0, payload.Length);
						client.OutputStream.Flush();
					}
					catch
					{
						(failed ?? (failed = new List<HttpListenerResponse>())).Add(client);
					}
				}
			}

			if (failed != null)
			{
				lock (clientsLock)
				{
					foreach (HttpListenerResponse client in failed)
					{
						clients.Remove(client);
					}
				}
				foreach (HttpListenerResponse client in failed)
				{
					try { client.OutputStream.Close(); } catch { }
				}
			}
		}

		private void AcceptLoop()
		{
			while (running)
			{
				try
				{
					HttpListenerContext context = listener.GetContext();
					ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
				}
				catch (HttpListenerException)
				{
					break; // listener was stopped
				}
				catch (Exception ex)
				{
					Plugin.Log?.LogWarning($"NOTacMap accept loop error: {ex.Message}");
				}
			}
		}

		private void HandleRequest(HttpListenerContext context)
		{
			try
			{
				context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
				context.Response.Headers.Add("X-Frame-Options", "DENY");
				context.Response.Headers.Add("Referrer-Policy", "no-referrer");

				HttpListenerRequest request = context.Request;
				string path = request.Url.AbsolutePath;
				string method = request.HttpMethod;

				GuardDecision decision = guard.Evaluate(request.Headers["Host"], request.Headers["Origin"],
					request.RemoteEndPoint?.Address, method, path, request.Url.Query, request.Headers["Cookie"]);
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
					LogRejected(request, decision.Status);
					Reject(context, decision.Status, decision.Message);
					return;
				}

				if (path == "/events")
				{
					// Closed tabs only drop out of the list when a write to them
					// fails, and the first write to a dead socket can still
					// succeed. Ping twice before turning anyone away, so a few
					// reloads in a row never fill the list with ghosts.
					if (ClientCount >= MaxSseClients)
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
						clients.Add(response);
					}

					// Headers only go out with the first write. Sending one
					// comment now lets the page see the connection open at
					// once instead of waiting for the next snapshot or ping.
					lock (writeLock)
					{
						response.OutputStream.Write(KeepAlive, 0, KeepAlive.Length);
						response.OutputStream.Flush();
					}
					// Response is left open; Broadcast()/Stop() write to and close it.
					return;
				}

				if (path == "/" || path == "/index.html")
				{
					ServeFile(context, "index.html", "text/html");
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

				if (path == "/mapinfo")
				{
					if (MapImageCapture.Png == null)
					{
						context.Response.StatusCode = 404;
						context.Response.Close();
						return;
					}
					string json = "{"
						+ $"\"mapSizeX\":{MapImageCapture.MapSizeX.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
						+ $"\"mapSizeY\":{MapImageCapture.MapSizeY.ToString(System.Globalization.CultureInfo.InvariantCulture)},"
						+ $"\"mapName\":\"{MapImageCapture.MapName}\""
						+ "}";
					byte[] bytes = Encoding.UTF8.GetBytes(json);
					context.Response.ContentType = "application/json";
					context.Response.ContentLength64 = bytes.Length;
					context.Response.OutputStream.Write(bytes, 0, bytes.Length);
					context.Response.Close();
					return;
				}

				if (path == "/lan")
				{
					string json = lanAddress == null
						? "{\"enabled\":false}"
						: "{\"enabled\":true,\"url\":\"" + LanUrl + "\"}";
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
						File.WriteAllText(settingsPath, body);
						context.Response.StatusCode = 204;
						context.Response.Close();
						return;
					}

					string json = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
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

		// Says once per device when something on the network is turned away, so a
		// phone that opened the wrong link shows up in the log. Requests from this
		// PC are not logged.
		private void LogRejected(HttpListenerRequest request, int status)
		{
			IPAddress from = request.RemoteEndPoint?.Address;
			if (from == null || IPAddress.IsLoopback(from)) return;
			string key = from.ToString();
			lock (rejectedLogged)
			{
				if (rejectedLogged.Count >= 32 || !rejectedLogged.Add(key)) return;
			}
			Plugin.Log?.LogInfo($"NOTacMap: turned away a request from {key} (status {status}).");
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
