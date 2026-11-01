using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NOTacMap
{
	// The answer for one request. Status 0 means carry on and handle it. 302
	// means redirect to RedirectTo and set the cookie. Anything else is the
	// status to send back.
	internal class GuardDecision
	{
		public int Status;
		public string SetCookie;
		public string RedirectTo;
		public string Message; // shown to the person who was turned away
		public string Reason;  // for the log, never contains the secret

		public static GuardDecision Allow() { return new GuardDecision(); }
		public static GuardDecision Reject(int status, string reason, string message = null)
		{
			return new GuardDecision { Status = status, Reason = reason, Message = message };
		}
	}

	// Decides whether a request is let through, using only plain values (no
	// HttpListener types) so the rules can be tested on their own.
	//
	// Requests from this PC (loopback) need no token. When LAN access is on,
	// requests from other devices must come from a private address and carry
	// the secret token, first as ?t=... in the link and afterwards as a cookie.
	internal class RequestGuard
	{
		public const string CookieName = "notacmap_t";
		// On the https link the cookie is Secure and has the __Host- prefix, which makes a browser take it only from an https page, only for
		// this exact host, with Path=/ and no Domain. Either name is accepted when a request comes in.
		public const string SecureCookieName = "__Host-notacmap_t";

		// The token ends up in a link, a cookie and a JSON string, so only plain characters are
		// allowed, and it has to be long enough that guessing it is hopeless.
		public static bool IsValidToken(string token)
		{
			if (string.IsNullOrEmpty(token) || token.Length < 16 || token.Length > 128) return false;
			foreach (char c in token)
			{
				bool plain = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-' || c == '_';
				if (!plain) return false;
			}
			return true;
		}

		private readonly HashSet<string> allowedHosts;
		private readonly byte[] token; // null when LAN access is off
		private readonly string httpsHost; // "address:port" of the HTTPS link, null while HTTPS is off
		private readonly bool httpsOnly;   // while HTTPS is on, other devices may only come through it

		public RequestGuard(int port, string lanAddress, string lanToken, int httpsPort = 0, bool httpsOnly = false)
		{
			allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"localhost:" + port,
				"127.0.0.1:" + port
			};
			if (lanAddress != null && !string.IsNullOrEmpty(lanToken))
			{
				allowedHosts.Add(lanAddress + ":" + port);
				token = Encoding.UTF8.GetBytes(lanToken);
				if (httpsPort > 0)
				{
					httpsHost = lanAddress + ":" + httpsPort;
					allowedHosts.Add(httpsHost);
					this.httpsOnly = httpsOnly;
				}
			}
		}

		// fetchSite is the Sec-Fetch-Site header, which current browsers add to every request. viaFront says that the request came
		// in through the TLS front (FrontPeers.IsFronted), remote is then the phone's address.
		public GuardDecision Evaluate(string host, string origin, IPAddress remote, string method,
			string path, string query, string cookieHeader, string fetchSite = null, bool viaFront = false)
		{
			// Only our own Host values are served. Stops a web page from
			// reaching us through DNS rebinding (its own domain pointed at one of
			// our addresses).
			if (host == null || !allowedHosts.Contains(host))
			{
				return GuardDecision.Reject(403, "address in the request is not one of ours");
			}

			// A request from another site carries its own Origin. Our own page
			// sends none on GETs and our own address on POSTs.
			if (origin != null)
			{
				// http:// for any of our addresses, https:// only for the HTTPS link while it is on.
				const string prefix = "http://";
				const string securePrefix = "https://";
				bool ours = (origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
						&& allowedHosts.Contains(origin.Substring(prefix.Length)))
					|| (httpsHost != null && origin.StartsWith(securePrefix, StringComparison.OrdinalIgnoreCase)
						&& string.Equals(origin.Substring(securePrefix.Length), httpsHost, StringComparison.OrdinalIgnoreCase));
				if (!ours)
				{
					return GuardDecision.Reject(403, "request comes from another site");
				}
			}

			// The event stream is only ever opened by our own page, which is same-origin. Something
			// else asking for it (an image tag on another site, a page on another local port) would
			// hold one of the few stream slots and lock the map out. A browser that sends no header
			// at all is let through.
			if (path == "/events" && fetchSite != null
				&& !fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
				&& !fetchSite.Equals("none", StringComparison.OrdinalIgnoreCase))
			{
				return GuardDecision.Reject(403, "request comes from another site");
			}

			bool settingsPost = method == "POST" && path == "/settings";
			bool lanPost = method == "POST" && path == "/lan";
			if (method != "GET" && method != "HEAD" && !settingsPost && !lanPost)
			{
				return GuardDecision.Reject(405, "method not allowed");
			}

			remote = Normalize(remote);
			bool secureHost = httpsHost != null && string.Equals(host, httpsHost, StringComparison.OrdinalIgnoreCase);
			// A request that names the https address can only come through the front, and the front's requests carry the phone's address.
			// One that looks local or has no address at all means the list of phones missed, or the front was changed. It is never let
			// in as this PC, whatever else is true.
			if (secureHost && (remote == null || IPAddress.IsLoopback(remote)))
			{
				return GuardDecision.Reject(403, "the secure link only answers through its own front");
			}
			if (remote != null && IPAddress.IsLoopback(remote))
			{
				return GuardDecision.Allow();
			}

			// With LAN access off the listener only takes connections from this PC,
			// so a request with no address at all is let through. An address that is
			// not this PC is refused.
			if (token == null)
			{
				return remote == null ? GuardDecision.Allow() : GuardDecision.Reject(403, "LAN access is off");
			}

			// From here on it is another device.
			if (!LanAddress.IsPrivate(remote))
			{
				return GuardDecision.Reject(403, "not a private network address");
			}
			// HttpsOnly: while the secure link runs, the plain link is for this PC only.
			if (httpsOnly && !viaFront)
			{
				return GuardDecision.Reject(403, "other devices may only use the secure link (HttpsOnly)",
					"This map only answers on the secure link. Open the link with https from the map page on the game PC.");
			}
			// Saving settings, the phone link and the LAN switch are for this PC only.
			if (settingsPost || path == "/lan")
			{
				return GuardDecision.Reject(403, "this page is for the game PC only");
			}

			string cookie = ReadCookie(cookieHeader, CookieName);
			string secureCookie = ReadCookie(cookieHeader, SecureCookieName);
			if ((cookie != null && TokenMatches(cookie)) || (secureCookie != null && TokenMatches(secureCookie)))
			{
				return GuardDecision.Allow();
			}

			string given = ReadQueryValue(query, "t", out string remaining);
			if (given != null && TokenMatches(given) && path != "/events")
			{
				// Move the token out of the address bar into a cookie. Never
				// redirect to a path that could point at another site. The cookie is
				// Lax, not Strict: a Strict cookie is not sent on the redirect after a
				// link opened from another app (a QR scanner, a chat), which left the
				// phone on the refusal page even with the right secret.
				string target = path.StartsWith("//", StringComparison.Ordinal) || path.Contains("\\") ? "/" : path;
				return new GuardDecision
				{
					Status = 302,
					RedirectTo = remaining.Length > 0 ? target + "?" + remaining : target,
					SetCookie = secureHost
						? SecureCookieName + "=" + Encoding.UTF8.GetString(token) + "; Path=/; Secure; HttpOnly; SameSite=Lax; Max-Age=31536000"
						: CookieName + "=" + Encoding.UTF8.GetString(token) + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=31536000"
				};
			}

			// Wrong secret and no secret are told apart in the log only.
			string reason = (cookie != null || secureCookie != null || given != null) ? "wrong secret" : "no secret in the request";
			return GuardDecision.Reject(401, reason, "Open the phone link or the QR code from the map page on the game PC.");
		}

		private static IPAddress Normalize(IPAddress a)
		{
			if (a != null && a.IsIPv4MappedToIPv6) return a.MapToIPv4();
			return a;
		}

		// Compares every byte, so the time taken doesn't say how much matched.
		private bool TokenMatches(string candidate)
		{
			byte[] c = Encoding.UTF8.GetBytes(candidate);
			int diff = c.Length ^ token.Length;
			for (int i = 0; i < token.Length; i++)
			{
				byte b = i < c.Length ? c[i] : (byte)0;
				diff |= token[i] ^ b;
			}
			return diff == 0;
		}

		internal static string ReadCookie(string header, string name)
		{
			if (string.IsNullOrEmpty(header)) return null;
			foreach (string part in header.Split(';'))
			{
				string p = part.Trim();
				int eq = p.IndexOf('=');
				if (eq > 0 && p.Substring(0, eq) == name) return p.Substring(eq + 1);
			}
			return null;
		}

		// Returns the value of one query parameter and the rest of the query
		// without it. The query may or may not start with '?'.
		internal static string ReadQueryValue(string query, string name, out string remaining)
		{
			remaining = "";
			if (string.IsNullOrEmpty(query)) return null;
			if (query[0] == '?') query = query.Substring(1);
			string value = null;
			var rest = new List<string>();
			foreach (string pair in query.Split('&'))
			{
				if (pair.Length == 0) continue;
				int eq = pair.IndexOf('=');
				string key = eq < 0 ? pair : pair.Substring(0, eq);
				if (key == name && value == null)
				{
					try { value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1)); }
					catch (UriFormatException) { value = ""; }
				}
				else
				{
					rest.Add(pair);
				}
			}
			remaining = string.Join("&", rest.ToArray());
			return value;
		}
	}

	// The HTTPS phone link is a TLS front on this PC that passes the bytes on to the web server (TlsFront). Every connection it opens to the
	// web server is noted here, before the first byte, with the address of the phone it belongs to. The web server then sees a connection
	// from this PC's own network address and asks this list who it really is. Only the front adds to the list, so nothing on the PC can
	// pass itself off as a phone, and a request that is not in the list is judged by the address it comes from, as before.
	internal static class FrontPeers
	{
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPAddress> peers =
			new System.Collections.Concurrent.ConcurrentDictionary<string, IPAddress>();

		public static void Add(IPEndPoint backend, IPAddress phone) { peers[Key(backend)] = phone; }
		public static void Remove(IPEndPoint backend) { IPAddress gone; peers.TryRemove(Key(backend), out gone); }
		public static int Count { get { return peers.Count; } }

		// True when this connection was opened by the front, so the request came through the secure link.
		public static bool IsFronted(IPEndPoint remote) { return remote != null && peers.ContainsKey(Key(remote)); }

		// The address a request really comes from.
		public static IPAddress Resolve(IPEndPoint remote)
		{
			if (remote == null) return null;
			IPAddress phone;
			return peers.TryGetValue(Key(remote), out phone) ? phone : remote.Address;
		}

		private static string Key(IPEndPoint e)
		{
			IPAddress a = e.Address;
			if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
			return a + ":" + e.Port;
		}
	}

	// Picks and checks the address LAN access listens on.
	internal static class LanAddress
	{
		// 10/8, 172.16/12, 192.168/16 and 169.254/16. Deliberately not
		// 100.64/10 (carrier-grade NAT), which can reach other customers.
		public static bool IsPrivate(IPAddress a)
		{
			if (a == null) return false;
			if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
			if (a.AddressFamily != AddressFamily.InterNetwork) return false;
			byte[] b = a.GetAddressBytes();
			return b[0] == 10
				|| (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
				|| (b[0] == 192 && b[1] == 168)
				|| (b[0] == 169 && b[1] == 254);
		}

		// An address given in the config, or the best private one on this PC.
		// Returns null, with the reason, when there is nothing safe to use.
		public static string Resolve(string configured, out string problem)
		{
			problem = null;
			if (!string.IsNullOrWhiteSpace(configured))
			{
				IPAddress parsed;
				if (!IPAddress.TryParse(configured.Trim(), out parsed) || !IsPrivate(parsed))
				{
					problem = "LanAddress must be a private IPv4 address (192.168.x.x, 10.x.x.x, 172.16-31.x.x or 169.254.x.x)";
					return null;
				}
				return parsed.ToString();
			}

			var found = new List<KeyValuePair<IPAddress, bool>>();
			try
			{
				foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
				{
					if (nic.OperationalStatus != OperationalStatus.Up) continue;
					if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
						|| nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
					IPInterfaceProperties props = nic.GetIPProperties();
					bool hasGateway = false;
					foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
					{
						if (g.Address != null && !g.Address.Equals(IPAddress.Any)) hasGateway = true;
					}
					foreach (UnicastIPAddressInformation u in props.UnicastAddresses)
					{
						found.Add(new KeyValuePair<IPAddress, bool>(u.Address, hasGateway));
					}
				}
			}
			catch (Exception ex)
			{
				problem = "couldn't read the network adapters (" + ex.Message + ")";
				return null;
			}

			IPAddress best = Pick(found);
			if (best == null) problem = "no private network address found on this PC";
			return best == null ? null : best.ToString();
		}

		// The first private IPv4 address, preferring an adapter with a gateway
		// (the one that is actually on your home network).
		public static IPAddress Pick(IList<KeyValuePair<IPAddress, bool>> candidates)
		{
			IPAddress firstPrivate = null;
			foreach (var c in candidates)
			{
				if (!IsPrivate(c.Key)) continue;
				if (c.Value) return c.Key;
				if (firstPrivate == null) firstPrivate = c.Key;
			}
			return firstPrivate;
		}
	}
}
