using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Mono.Security.X509;
using Mono.Security.X509.Extensions;
using MonoCertificate = Mono.Security.X509.X509Certificate;

namespace NOTacMap
{
	// The certificate for the HTTPS phone link. It is made inside the game with the game's own Mono.Security (the newer .NET calls for this
	// are missing in the game's runtime, found in the test of 2026-10-25). It is signed by itself, so the phone's browser warns once and the
	// person taps through, which is the price of HTTPS on a home network. It names only the network address of the PC, is kept in a file so it
	// stays the same between starts, and is made again when the address changes or when it has under 30 days left. What is true of the
	// certificate is read from the certificate itself, there is no side file that could disagree with it.
	internal static class TlsCertificate
	{
		// The file stays on this PC, the password is only there because the format asks for one.
		private const string Password = "notacmap";
		private const string FileName = "https-cert.pfx";
		private const int Days = 365;
		private const string SanOid = "2.5.29.17";
		private const string KeyUsageOid = "2.5.29.15";
		private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

		// What makes the PKCS12 bytes. The tests put another one in, because the game's Mono.Security only runs inside the game.
		internal static Func<string, DateTime, byte[]> Maker = Create;

		// Older builds kept the files next to the plugin. They are removed, the key does not belong there.
		internal static void RemoveOldFiles(string pluginFolder)
		{
			foreach (string name in new[] { FileName, "https-cert.txt" })
			{
				try
				{
					string old = Path.Combine(pluginFolder, name);
					if (File.Exists(old)) File.Delete(old);
				}
				catch
				{
					// not allowed to delete: it just stays where it is
				}
			}
		}

		internal static X509Certificate2 LoadOrCreate(string folder, string address)
		{
			string pfxPath = Path.Combine(folder, FileName);
			try
			{
				if (File.Exists(pfxPath))
				{
					var kept = new X509Certificate2(pfxPath, Password);
					if (IsGoodFor(kept, address)) return kept;
				}
			}
			catch
			{
				// unreadable or damaged: a new one is made
			}

			byte[] pfx = Maker(address, DateTime.UtcNow.AddDays(Days));
			try
			{
				Directory.CreateDirectory(folder);
				File.WriteAllBytes(pfxPath, pfx);
			}
			catch (Exception ex)
			{
				Plugin.Log?.LogWarning("NOTacMap: couldn't save the HTTPS certificate (" + ex.Message + "), a new one is made each time the game starts and the phone warns again.");
			}
			return new X509Certificate2(pfx, Password);
		}

		// Has its key, names exactly this address, and is good for at least 30 more days.
		internal static bool IsGoodFor(X509Certificate2 certificate, string address)
		{
			if (certificate == null || !certificate.HasPrivateKey) return false;
			if (certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30)) return false;
			if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow) return false;
			if (!KeyUsageIsSound(certificate)) return false;
			string[] names = AddressesInCertificate(certificate);
			return names != null && names.Length == 1 && names[0] == address;
		}

		// The key usage is there, says digital signature, and is written the way DER wants it. A certificate with it written wrongly is
		// refused by Chrome as ERR_CERT_INVALID while OpenSSL and .NET read it without complaint: the game's Mono.Security wrote two flags
		// with the unused bits of one, so a certificate made like that is made again.
		internal static bool KeyUsageIsSound(X509Certificate2 certificate)
		{
			foreach (System.Security.Cryptography.X509Certificates.X509Extension extension in certificate.Extensions)
			{
				if (extension.Oid != null && extension.Oid.Value == KeyUsageOid) return KeyUsageBytesAreSound(extension.RawData);
			}
			return false;
		}

		// BIT STRING: tag 3, length, number of unused bits (0 to 7), the bits. The unused bits have to be zero and the last byte has to
		// have a set bit (DER writes no more bits than it needs), and the first bit is digital signature.
		internal static bool KeyUsageBytesAreSound(byte[] der)
		{
			if (der == null || der.Length < 4 || der[0] != 0x03) return false;
			int length = der[1];
			if (length < 2 || length > 3 || der.Length != 2 + length) return false;
			int unused = der[2];
			if (unused > 7) return false;
			byte last = der[der.Length - 1];
			if (last == 0 || (last & ((1 << unused) - 1)) != 0) return false;
			if ((last & (1 << unused)) == 0) return false;   // the last byte's lowest used bit must be set
			return (der[3] & 0x80) != 0;
		}

		// The network addresses in the certificate's subject alternative name, read from the raw bytes (the text form of the extension
		// differs between runtimes). Null when there is anything else in it (a name) or the extension is not there or not readable.
		internal static string[] AddressesInCertificate(X509Certificate2 certificate)
		{
			foreach (System.Security.Cryptography.X509Certificates.X509Extension extension in certificate.Extensions)
			{
				if (extension.Oid == null || extension.Oid.Value != SanOid) continue;
				byte[] der = extension.RawData;
				try
				{
					int at = 0;
					if (der.Length < 2 || der[at++] != 0x30) return null;
					int length = ReadLength(der, ref at);
					int end = at + length;
					if (end > der.Length) return null;
					var found = new ArrayList();
					while (at < end)
					{
						byte tag = der[at++];
						int len = ReadLength(der, ref at);
						if (at + len > end) return null;
						if (tag != 0x87 || (len != 4 && len != 16)) return null;   // anything but an IP address
						var bytes = new byte[len];
						Array.Copy(der, at, bytes, 0, len);
						found.Add(new System.Net.IPAddress(bytes).ToString());
						at += len;
					}
					return (string[])found.ToArray(typeof(string));
				}
				catch
				{
					return null;
				}
			}
			return null;
		}

		private static int ReadLength(byte[] der, ref int at)
		{
			int first = der[at++];
			if (first < 0x80) return first;
			int count = first & 0x7F;
			if (count < 1 || count > 3) throw new FormatException("length");
			int value = 0;
			for (int i = 0; i < count; i++) value = (value << 8) | der[at++];
			return value;
		}

		// SHA-256 of the certificate, as pairs of hex digits with colons. The phone shows the same under the certificate details.
		internal static string Fingerprint(X509Certificate2 certificate)
		{
			using (var sha = SHA256.Create())
			{
				byte[] hash = sha.ComputeHash(certificate.RawData);
				var text = new StringBuilder();
				for (int i = 0; i < hash.Length; i++)
				{
					if (i > 0) text.Append(':');
					text.Append(hash[i].ToString("X2"));
				}
				return text.ToString();
			}
		}

		// A PKCS12 file in memory with the certificate and its key.
		internal static byte[] Create(string address, DateTime notAfter)
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

			// Not a certificate authority, for a server only, and only for the network address of this PC.
			var basic = new BasicConstraintsExtension();
			basic.CertificateAuthority = false;
			basic.Critical = true;
			builder.Extensions.Add(basic);
			var usage = new KeyUsageExtension();
			// Signing only: the connection uses ECDHE, which needs just the signature. (The game's Mono.Security writes only this bit even
			// when keyEncipherment is asked for, so this says what the file really has, and no key exchange by encryption is allowed.)
			usage.KeyUsage = KeyUsages.digitalSignature;
			usage.Critical = true;
			builder.Extensions.Add(usage);
			var purpose = new ExtendedKeyUsageExtension();
			purpose.KeyPurpose.Add(ServerAuthOid);
			builder.Extensions.Add(purpose);
			builder.Extensions.Add(new SubjectAltNameExtension(null, null, new[] { address }, null));
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
