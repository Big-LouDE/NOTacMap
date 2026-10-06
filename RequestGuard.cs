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

		public RequestGuard(int port, string lanAddress, string lanToken)
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
			}
		}

		public GuardDecision Evaluate(string host, string origin, IPAddress remote, string method,
			string path, string query, string cookieHeader)
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
				const string prefix = "http://";
				if (!origin.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
					|| !allowedHosts.Contains(origin.Substring(prefix.Length)))
				{
					return GuardDecision.Reject(403, "request comes from another site");
				}
			}

			bool settingsPost = method == "POST" && path == "/settings";
			bool lanPost = method == "POST" && path == "/lan";
			if (method != "GET" && method != "HEAD" && !settingsPost && !lanPost)
			{
				return GuardDecision.Reject(405, "method not allowed");
			}

			remote = Normalize(remote);
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
			// Saving settings, the phone link and the LAN switch are for this PC only.
			if (settingsPost || path == "/lan")
			{
				return GuardDecision.Reject(403, "this page is for the game PC only");
			}

			string cookie = ReadCookie(cookieHeader, CookieName);
			if (cookie != null && TokenMatches(cookie))
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
					SetCookie = CookieName + "=" + Encoding.UTF8.GetString(token)
						+ "; Path=/; HttpOnly; SameSite=Lax; Max-Age=31536000"
				};
			}

			// Wrong secret and no secret are told apart in the log only.
			string reason = (cookie != null || given != null) ? "wrong secret" : "no secret in the request";
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
