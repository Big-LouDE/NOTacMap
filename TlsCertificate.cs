using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mono.Security.X509;
using Mono.Security.X509.Extensions;
using MonoCertificate = Mono.Security.X509.X509Certificate;

namespace NOTacMap
{
	// The certificate for the HTTPS phone link. It is made inside the game with the game's own Mono.Security (the newer .NET calls for this
	// are missing in the game's runtime, found in the test of 2026-10-25). It is signed by itself, so the phone's browser warns once and the
	// person taps through, which is the price of HTTPS on a home network. It names the network address of the PC, is kept in a file next to the
	// plugin so it stays the same between starts, and is made again when the address changes or when it has under 30 days left.
	internal static class TlsCertificate
	{
		// The file stays on this PC, the password is only there because the format asks for one.
		private const string Password = "notacmap";
		private const int Days = 365;

		internal static X509Certificate2 LoadOrCreate(string folder, string address)
		{
			string pfxPath = Path.Combine(folder, "https-cert.pfx");
			string notePath = Path.Combine(folder, "https-cert.txt");
			try
			{
				if (File.Exists(pfxPath) && File.Exists(notePath))
				{
					string[] note = File.ReadAllLines(notePath);
					DateTime until;
					if (note.Length >= 2 && note[0] == address
						&& DateTime.TryParse(note[1], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out until)
						&& until.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
					{
						var kept = new X509Certificate2(pfxPath, Password);
						if (kept.HasPrivateKey)
						{
							return kept;
						}
					}
				}
			}
			catch
			{
				// unreadable or damaged: a new one is made
			}

			DateTime notAfter = DateTime.UtcNow.AddDays(Days);
			byte[] pfx = Create(address, notAfter);
			try
			{
				File.WriteAllBytes(pfxPath, pfx);
				File.WriteAllLines(notePath, new[] { address, notAfter.ToString("o") });
			}
			catch (Exception ex)
			{
				Plugin.Log?.LogWarning("NOTacMap: couldn't save the HTTPS certificate next to the plugin (" + ex.Message + "), a new one is made each time the game starts and the phone warns again.");
			}
			return new X509Certificate2(pfx, Password);
		}

		// A PKCS12 file in memory with the certificate and its key.
		private static byte[] Create(string address, DateTime notAfter)
		{
			var rsa = new RSACryptoServiceProvider(2048);
			var builder = new X509CertificateBuilder(3);
			byte[] serial = Guid.NewGuid().ToByteArray();
			serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
			builder.SerialNumber = serial;
			builder.IssuerName = "CN=NOTacMap";
			builder.SubjectName = "CN=NOTacMap";
			builder.NotBefore = DateTime.UtcNow.AddDays(-1);
			builder.NotAfter = notAfter;
			builder.SubjectPublicKey = rsa;
			builder.Hash = "SHA256";
			builder.Extensions.Add(new SubjectAltNameExtension(null, new[] { "localhost" }, new[] { address, "127.0.0.1" }, null));
			byte[] der = builder.Sign(rsa);

			var p12 = new PKCS12();
			p12.Password = Password;
			var attributes = new Hashtable();
			attributes.Add(PKCS9.localKeyId, new ArrayList { new byte[] { 1, 0, 0, 0 } });
			p12.AddCertificate(new MonoCertificate(der), attributes);
			p12.AddPkcs8ShroudedKeyBag(rsa, attributes);
			return p12.GetBytes();
		}
	}
}
