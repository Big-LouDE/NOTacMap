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
		private readonly HttpListener listener;
		private readonly List<HttpListenerResponse> clients = new List<HttpListenerResponse>();
		private readonly object clientsLock = new object();
		private Thread acceptThread;
		private volatile bool running;

		public MapServer(int port)
		{
			listener = new HttpListener();
			listener.Prefixes.Add($"http://localhost:{port}/");
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
			listener.Start();
			acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "NOTacMap-Accept" };
			acceptThread.Start();
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
			byte[] payload = Encoding.UTF8.GetBytes($"data: {json}\n\n");

			// Snapshot the client list under the lock (fast), then do the
			// actual writes OUTSIDE it. HasClients (checked from Unity's
			// main thread every ~200ms) needs this same lock - holding it
			// for the duration of a potentially slow socket write would let
			// a lagging browser stall the main thread via lock contention,
			// exactly as bad as the original synchronous-write bug even
			// though the write itself already moved to a background thread.
			HttpListenerResponse[] snapshot;
			lock (clientsLock)
			{
				snapshot = clients.ToArray();
			}

			List<HttpListenerResponse> failed = null;
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
				string path = context.Request.Url.AbsolutePath;
				if (path == "/events")
				{
					HttpListenerResponse response = context.Response;
					response.ContentType = "text/event-stream";
					response.Headers.Add("Cache-Control", "no-cache");
					response.SendChunked = true;
					lock (clientsLock)
					{
						clients.Add(response);
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

				if (path == "/settings")
				{
					// Opaque pass-through storage: the server doesn't parse or
					// understand this JSON at all, just persists whatever blob
					// the client sends and hands the same blob back later. Keeps
					// the settings schema entirely client-owned - adding a new
					// filter checkbox in index.html never needs a matching C#
					// change here.
					string settingsPath = Path.Combine(Path.GetDirectoryName(typeof(MapServer).Assembly.Location), "settings.json");
					if (context.Request.HttpMethod == "POST")
					{
						string body;
						using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
						{
							body = reader.ReadToEnd();
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
