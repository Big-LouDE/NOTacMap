using System;
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
	internal sealed class TlsFront : IDisposable
	{
		private const int MaxConnections = 24;      // open TLS connections at the same time
		private const int HandshakeMs = 10000;      // a connection that does not finish its handshake in this time is dropped
		private readonly TcpListener listener;
		private readonly X509Certificate2 certificate;
		private readonly IPAddress address;
		private readonly int backendPort;
		private int open;
		private volatile bool stopped;

		private TlsFront(IPAddress address, int frontPort, int backendPort, X509Certificate2 certificate)
		{
			this.address = address;
			this.backendPort = backendPort;
			this.certificate = certificate;
			listener = new TcpListener(address, frontPort);
		}

		// Starts listening on the network address. Throws when the port is taken or Windows says no.
		internal static IDisposable Start(string address, int frontPort, int backendPort, X509Certificate2 certificate)
		{
			var front = new TlsFront(IPAddress.Parse(address), frontPort, backendPort, certificate);
			front.listener.Start();
			new Thread(front.AcceptLoop) { IsBackground = true, Name = "NOTacMap-TLS-Accept" }.Start();
			return front;
		}

		public void Dispose()
		{
			stopped = true;
			try { listener.Stop(); } catch { /* already stopped */ }
		}

		private void AcceptLoop()
		{
			while (!stopped)
			{
				TcpClient client;
				try
				{
					client = listener.AcceptTcpClient();
				}
				catch
				{
					return; // stopped
				}
				if (Interlocked.Increment(ref open) > MaxConnections)
				{
					Interlocked.Decrement(ref open);
					try { client.Close(); } catch { }
					continue;
				}
				TcpClient accepted = client;
				new Thread(() => Serve(accepted)) { IsBackground = true, Name = "NOTacMap-TLS" }.Start();
			}
		}

		private void Serve(TcpClient client)
		{
			TcpClient back = null;
			IPEndPoint backEnd = null;
			try
			{
				IPEndPoint phone = client.Client.RemoteEndPoint as IPEndPoint;
				client.NoDelay = true;
				client.ReceiveTimeout = HandshakeMs;
				client.SendTimeout = HandshakeMs;
				var ssl = new SslStream(client.GetStream(), false);
				// TLS 1.2, no client certificate (asking for one is what hangs Chrome).
				ssl.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
				client.ReceiveTimeout = 0;
				client.SendTimeout = 0;

				back = new TcpClient();
				back.NoDelay = true;
				back.Connect(address, backendPort);
				backEnd = (IPEndPoint)back.Client.LocalEndPoint;
				// Before any byte goes through: this connection to the web server is that phone.
				FrontPeers.Add(backEnd, phone.Address);
				NetworkStream backStream = back.GetStream();

				var down = new Thread(() => Copy(backStream, ssl)) { IsBackground = true, Name = "NOTacMap-TLS-down" };
				down.Start();
				Copy(ssl, backStream);
			}
			catch
			{
				// A handshake that failed, a phone that went away: nothing to report.
			}
			finally
			{
				if (backEnd != null) FrontPeers.Remove(backEnd);
				try { if (back != null) back.Close(); } catch { }
				try { client.Close(); } catch { }
				Interlocked.Decrement(ref open);
			}
		}

		// Copies until one side ends, and passes what arrives on at once (the live stream must not be held back).
		private static void Copy(Stream from, Stream to)
		{
			var buffer = new byte[16384];
			try
			{
				int n;
				while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
				{
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
	}
}
