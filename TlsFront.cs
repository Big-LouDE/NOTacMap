using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace NOTacMap
{
	// HTTPS for the phone link, experimental and off by default. The game's own HttpListener cannot do it for Chrome: it asks the browser for a
	// client certificate during the handshake and Chrome waits on that for ever (found in the game on 2026-11-01). So the encryption is done
	// here, on its own port: this class takes the TLS connection from the phone, opens an ordinary connection to the web server on the
	// same network address, and copies the bytes both ways as they are. The web server does not know about TLS.
	//
	// So that the guard still knows who it is talking to, each connection to the web server is noted in FrontPeers with the phone's
	// address before the first byte is sent. Nothing but this class adds to that list.
	//
	// Anything on the home network can open a connection to this port, so every stage has a limit: the handshake has a total time, a
	// connection that says nothing after the handshake is dropped, a phone that stops reading is dropped, and there are caps on the
	// connections of one address and on all of them. The live event stream is long lived on purpose, so after the first request
	// arrives there is no time limit on reading.
	internal sealed class TlsFront : IDisposable
	{
		// Internal and not constants so the tests can make them short.
		internal static int HandshakeMs = 10000;      // the whole TLS handshake, not each read
		internal static int FirstRequestMs = 10000;   // after the handshake, the first byte of a request has to come this soon
		internal static int MaxPerAddress = 8;        // a browser opens up to 6 connections to one address, plus a stream
		internal static bool TestFaultOnce;           // tests only: the accept loop throws once
		private const int SendTimeoutMs = 30000;      // a phone that stops reading is let go after this
		private const int MaxConnections = 24;        // open TLS connections at the same time
		private const int BindTries = 3;              // the port of a front that was just closed can take a moment to come free

		private sealed class Link
		{
			public TcpClient Client;
			public TcpClient Back;
		}

		private readonly TcpListener listener;
		private readonly X509Certificate2 certificate;
		private readonly IPAddress address;
		private readonly int backendPort;
		private readonly HashSet<Link> live = new HashSet<Link>();                       // lock (live)
		private readonly Dictionary<string, int> perAddress = new Dictionary<string, int>(); // lock (live)
		private readonly Dictionary<string, DateTime> lastLogged = new Dictionary<string, DateTime>(); // lock (lastLogged)
		private int open;
		private volatile bool stopped;

		private TlsFront(IPAddress address, TcpListener listener, int backendPort, X509Certificate2 certificate)
		{
			this.address = address;
			this.listener = listener;
			this.backendPort = backendPort;
			this.certificate = certificate;
		}

		// Starts listening on the network address. Throws when the port stays taken or Windows says no.
		internal static IDisposable Start(string address, int frontPort, int backendPort, X509Certificate2 certificate)
		{
			IPAddress ip = IPAddress.Parse(address);
			Exception last = null;
			for (int i = 1; i <= BindTries; i++)
			{
				var listener = new TcpListener(ip, frontPort);
				try
				{
					listener.Start();
					var front = new TlsFront(ip, listener, backendPort, certificate);
					new Thread(front.AcceptLoop) { IsBackground = true, Name = "NOTacMap-TLS-Accept" }.Start();
					return front;
				}
				catch (Exception ex)
				{
					last = ex;
					try { listener.Stop(); } catch { }
					if (i < BindTries) Thread.Sleep(300);
				}
			}
			throw last;
		}

		// Stops listening and closes every connection that is still open, so nothing outlives the front.
		public void Dispose()
		{
			stopped = true;
			try { listener.Stop(); } catch { /* already stopped */ }
			Link[] open;
			lock (live)
			{
				open = new Link[live.Count];
				live.CopyTo(open);
			}
			foreach (Link link in open) Kill(link);
		}

		private void AcceptLoop()
		{
			while (!stopped)
			{
				TcpClient client;
				try
				{
					if (TestFaultOnce)
					{
						TestFaultOnce = false;
						throw new InvalidOperationException("test fault in the accept loop");
					}
					client = listener.AcceptTcpClient();
				}
				catch (Exception ex)
				{
					if (stopped) return;   // closed on purpose
					Note(null, ex);
					Thread.Sleep(100);
					continue;
				}

				string who = KeyOf(client.Client.RemoteEndPoint as IPEndPoint);
				if (!Admit(who))
				{
					try { client.Close(); } catch { }
					continue;
				}
				try
				{
					TcpClient accepted = client;
					new Thread(() => Serve(accepted, who)) { IsBackground = true, Name = "NOTacMap-TLS" }.Start();
				}
				catch (Exception ex)
				{
					Release(who);
					try { client.Close(); } catch { }
					Note(who, ex);
				}
			}
		}

		// The global cap and the cap for one address. Counted from here until Serve ends.
		private bool Admit(string who)
		{
			lock (live)
			{
				int count;
				perAddress.TryGetValue(who, out count);
				if (open >= MaxConnections || count >= MaxPerAddress) return false;
				open++;
				perAddress[who] = count + 1;
				return true;
			}
		}

		private void Release(string who)
		{
			lock (live)
			{
				open--;
				int count;
				if (perAddress.TryGetValue(who, out count))
				{
					if (count <= 1) perAddress.Remove(who); else perAddress[who] = count - 1;
				}
			}
		}

		private void Serve(TcpClient client, string who)
		{
			var link = new Link { Client = client };
			IPEndPoint phone = client.Client.RemoteEndPoint as IPEndPoint;
			IPEndPoint backEnd = null;
			Timer handshakeTimer = null;
			Timer firstRequestTimer = null;
			try
			{
				lock (live) live.Add(link);
				if (stopped) return;   // the front was closed while this connection was being set up
				client.NoDelay = true;
				KeepAlive(client.Client);

				// The whole handshake has a time limit of its own. A read timeout alone would let a sender that drips one byte at a time hold
				// the slot for ever, so a timer closes the socket and the handshake ends with an error.
				handshakeTimer = new Timer(_ => Kill(link), null, HandshakeMs, Timeout.Infinite);
				var ssl = new SslStream(client.GetStream(), false);
				// TLS 1.2, no client certificate (asking for one is what hangs Chrome).
				ssl.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
				handshakeTimer.Dispose();
				handshakeTimer = null;
				if (!ssl.IsEncrypted || !ssl.IsAuthenticated) return;

				client.ReceiveTimeout = 0;
				client.SendTimeout = SendTimeoutMs;

				TcpClient back = new TcpClient();
				lock (live) link.Back = back;
				back.NoDelay = true;
				KeepAlive(back.Client);
				back.Connect(address, backendPort);
				back.SendTimeout = SendTimeoutMs;
				backEnd = (IPEndPoint)back.Client.LocalEndPoint;
				// Before any byte goes through: this connection to the web server is that phone.
				FrontPeers.Add(backEnd, phone.Address);
				NetworkStream backStream = back.GetStream();

				// Nothing arrives after the handshake: let go. Once the first byte is there, reading has no time limit.
				firstRequestTimer = new Timer(_ => Kill(link), null, FirstRequestMs, Timeout.Infinite);
				var down = new Thread(() => Copy(backStream, ssl, null)) { IsBackground = true, Name = "NOTacMap-TLS-down" };
				down.Start();
				Timer toStop = firstRequestTimer;
				Copy(ssl, backStream, () => toStop.Dispose());
			}
			catch (Exception ex)
			{
				// A handshake that failed, a phone that went away, a limit that closed the connection: nothing for the log at the normal level.
				Note(who, ex);
			}
			finally
			{
				if (handshakeTimer != null) handshakeTimer.Dispose();
				if (firstRequestTimer != null) firstRequestTimer.Dispose();
				// The entry goes before the connection is closed on purpose. Until the close the local port is still ours, so no new
				// connection can have the same endpoint and a leftover entry could never be mistaken for another phone. Do not reorder.
				if (backEnd != null) FrontPeers.Remove(backEnd);
				Kill(link);
				lock (live) live.Remove(link);
				Release(who);
			}
		}

		private static void Kill(Link link)
		{
			TcpClient client = link.Client;
			TcpClient back = link.Back;
			try { if (client != null) client.Close(); } catch { }
			try { if (back != null) back.Close(); } catch { }
		}

		// Copies until one side ends, and passes what arrives on at once (the live stream must not be held back).
		private static void Copy(Stream from, Stream to, Action onFirstBytes)
		{
			var buffer = new byte[16384];
			try
			{
				int n;
				while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
				{
					if (onFirstBytes != null)
					{
						onFirstBytes();
						onFirstBytes = null;
					}
					to.Write(buffer, 0, n);
					to.Flush();
				}
			}
			catch
			{
				// the other side closed
			}
			try { to.Close(); } catch { }
		}

		// A dead phone (battery pulled, out of wifi range) would otherwise keep its slot for hours. The system sends a probe after
		// 30 seconds of silence and every 10 seconds after that. Some runtimes do not take the call, then the plain option is all there is.
		private static void KeepAlive(Socket socket)
		{
			try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
			try
			{
				var values = new byte[12];
				BitConverter.GetBytes(1).CopyTo(values, 0);        // on
				BitConverter.GetBytes(30000).CopyTo(values, 4);    // idle time in ms
				BitConverter.GetBytes(10000).CopyTo(values, 8);    // interval in ms
				socket.IOControl(IOControlCode.KeepAliveValues, values, null);
			}
			catch { }
		}

		private static string KeyOf(IPEndPoint endPoint)
		{
			if (endPoint == null) return "unknown";
			IPAddress a = endPoint.Address;
			if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
			return a.ToString();
		}

		// Debug level only, and one line per address per minute, so a device that keeps failing cannot fill the log.
		private void Note(string who, Exception ex)
		{
			string key = who ?? "front";
			lock (lastLogged)
			{
				DateTime last;
				if (lastLogged.TryGetValue(key, out last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1)) return;
				if (lastLogged.Count > 64) lastLogged.Clear();
				lastLogged[key] = DateTime.UtcNow;
			}
			Plugin.Log?.LogDebug("NOTacMap: HTTPS connection from " + key + " ended: " + ex.GetType().Name);
		}
	}
}
