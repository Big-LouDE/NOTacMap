// Tests for TlsCertificate: when the certificate for the HTTPS phone link is kept and when it is made again, how it is read, the fingerprint,
// and (with a file made in the game) what the game's own Mono.Security put into it.
//   dotnet run --project tests/CertTests -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Nuclear Option"
//   dotnet run --project tests/CertTests -c Release -p:GameDir="..." -- "<path to a https-cert.pfx made by the game>" <address in it>
// The game's Mono.Security.dll only runs inside the game (it needs a Mono-only runtime method), so the tests make their own certificate of
// the same shape with .NET and put it into TlsCertificate.Maker. The certificate the game really makes is checked with the second form.
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using NOTacMap;

namespace NOTacMap
{
    internal class FakeLog
    {
        public void LogWarning(string m) { Console.WriteLine("  (log) " + m); }
    }
    internal static class Plugin
    {
        public static FakeLog Log = new FakeLog();
    }
}

internal static class Program
{
    static int pass, fail;
    static void Check(string name, bool ok) { if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + name); } }
    const string Password = "notacmap";

    // The same shape as the game makes: CA false (critical), digital signature only, server authentication, the address as the only name.
    static byte[] MakeLike(string address, DateTime notAfter)
    {
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=NOTacMap", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Parse(address));
            request.CertificateExtensions.Add(san.Build());
            var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), new DateTimeOffset(notAfter));
            return made.Export(X509ContentType.Pfx, Password);
        }
    }

    static void CheckShape(string label, X509Certificate2 c, string address, bool exactYear)
    {
        bool sawBasic = false, sawUsage = false, sawPurpose = false, caFalse = false, basicCritical = false, usageOk = false, serverAuth = false;
        foreach (X509Extension e in c.Extensions)
        {
            var basic = e as X509BasicConstraintsExtension;
            if (basic != null) { sawBasic = true; caFalse = !basic.CertificateAuthority; basicCritical = basic.Critical; }
            var usage = e as X509KeyUsageExtension;
            if (usage != null) { sawUsage = true; usageOk = usage.KeyUsages == X509KeyUsageFlags.DigitalSignature; }
            var eku = e as X509EnhancedKeyUsageExtension;
            if (eku != null) { sawPurpose = true; serverAuth = eku.EnhancedKeyUsages.Count == 1 && eku.EnhancedKeyUsages[0].Value == "1.3.6.1.5.5.7.3.1"; }
        }
        Check(label + ": basic constraints are there, not a CA, critical", sawBasic && caFalse && basicCritical);
        Check(label + ": key usage is digital signature only", sawUsage && usageOk);
        Check(label + ": extended key usage is exactly server authentication", sawPurpose && serverAuth);
        string[] names = TlsCertificate.AddressesInCertificate(c);
        Check(label + ": it names exactly the network address, nothing else", names != null && names.Length == 1 && names[0] == address);
        double days = (c.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
        Check(label + ": it is good for a year or so", exactYear ? (days > 360 && days < 366) : days > 30);
        Check(label + ": it has its key", c.HasPrivateKey && c.PublicKey.Key.KeySize >= 2048);
        Check(label + ": it is not self-describing as a CA (subject = issuer is fine for a self-signed one)", c.Subject == c.Issuer);
    }

    static void Main(string[] args)
    {
        TlsCertificate.Maker = MakeLike;
        string dir = Path.Combine(Path.GetTempPath(), "notacmap-certtests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Run(dir);
            if (args.Length >= 2) CheckGameFile(args[0], args[1]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Console.WriteLine($"\n{pass} passed, {fail} failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }

    // A file the game made with its own Mono.Security.
    static void CheckGameFile(string path, string address)
    {
        var c = new X509Certificate2(path, Password);
        CheckShape("game file", c, address, false);
        Check("game file: IsGoodFor", TlsCertificate.IsGoodFor(c, address));
        Check("game file: a different address is not good", !TlsCertificate.IsGoodFor(c, "10.0.0.1"));
        Console.WriteLine("  game file fingerprint: " + TlsCertificate.Fingerprint(c));
    }

    static void Run(string dir)
    {
        const string A = "192.168.1.10", B = "192.168.1.20";
        var c = TlsCertificate.LoadOrCreate(dir, A);
        Check("a certificate is made", c != null && c.HasPrivateKey);
        Check("it is saved in the folder it was given", File.Exists(Path.Combine(dir, "https-cert.pfx")));
        Check("no side file", !File.Exists(Path.Combine(dir, "https-cert.txt")));
        CheckShape("made", c, A, true);

        // the fingerprint
        string fp = TlsCertificate.Fingerprint(c);
        Check("the fingerprint has 32 pairs with colons", fp.Length == 95 && fp.Split(':').Length == 32);
        Check("the fingerprint is the SHA-256 of the certificate", fp.Replace(":", "") == BitConverter.ToString(SHA256.Create().ComputeHash(c.RawData)).Replace("-", ""));
        Check("the fingerprint is upper case hex", fp == fp.ToUpperInvariant());

        // kept when it is still good, made again when the address is another one
        var again = TlsCertificate.LoadOrCreate(dir, A);
        Check("the same address keeps the same certificate", again.Thumbprint == c.Thumbprint);
        var moved = TlsCertificate.LoadOrCreate(dir, B);
        string[] movedNames = TlsCertificate.AddressesInCertificate(moved);
        Check("another address makes a new certificate for it", moved.Thumbprint != c.Thumbprint && movedNames.Length == 1 && movedNames[0] == B);
        Check("and the file now holds the new one", TlsCertificate.LoadOrCreate(dir, B).Thumbprint == moved.Thumbprint);

        // under 30 days left: made again
        File.WriteAllBytes(Path.Combine(dir, "https-cert.pfx"), MakeLike(B, DateTime.UtcNow.AddDays(10)));
        var old = new X509Certificate2(Path.Combine(dir, "https-cert.pfx"), Password);
        Check("a certificate with 10 days left is not good enough", !TlsCertificate.IsGoodFor(old, B));
        var renewed = TlsCertificate.LoadOrCreate(dir, B);
        Check("a certificate with 10 days left is made again", renewed.Thumbprint != old.Thumbprint && TlsCertificate.IsGoodFor(renewed, B));
        File.WriteAllBytes(Path.Combine(dir, "https-cert.pfx"), MakeLike(B, DateTime.UtcNow.AddDays(60)));
        Check("a certificate with 60 days left is kept", TlsCertificate.IsGoodFor(new X509Certificate2(Path.Combine(dir, "https-cert.pfx"), Password), B));
        // a damaged file is replaced
        File.WriteAllBytes(Path.Combine(dir, "https-cert.pfx"), new byte[] { 1, 2, 3 });
        var fixedUp = TlsCertificate.LoadOrCreate(dir, B);
        Check("a damaged file is replaced", TlsCertificate.IsGoodFor(fixedUp, B));
        // a folder that does not exist yet is made
        string deeper = Path.Combine(dir, "a", "b");
        var inNew = TlsCertificate.LoadOrCreate(deeper, A);
        Check("the folder is made when it is missing", inNew != null && File.Exists(Path.Combine(deeper, "https-cert.pfx")));
        // a certificate that names something else as well is not accepted for an address
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Parse(A)); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            var extra = new X509Certificate2(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300)).Export(X509ContentType.Pfx, "p"), "p");
            Check("a certificate with a name besides the address is not accepted", !TlsCertificate.IsGoodFor(extra, A));
            Check("and its names are not read as just addresses", TlsCertificate.AddressesInCertificate(extra) == null);
        }
        // one without the extension at all
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var bare = new X509Certificate2(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(300)).Export(X509ContentType.Pfx, "p"), "p");
            Check("a certificate without an alternative name is not accepted", TlsCertificate.AddressesInCertificate(bare) == null && !TlsCertificate.IsGoodFor(bare, A));
        }
        Check("nothing is not accepted", !TlsCertificate.IsGoodFor(null, A));

        // the old place next to the plugin
        string plugin = Path.Combine(dir, "plugin");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "https-cert.pfx"), "x");
        File.WriteAllText(Path.Combine(plugin, "https-cert.txt"), "x");
        File.WriteAllText(Path.Combine(plugin, "index.html"), "keep");
        TlsCertificate.RemoveOldFiles(plugin);
        Check("old files next to the plugin are removed, nothing else", !File.Exists(Path.Combine(plugin, "https-cert.pfx")) && !File.Exists(Path.Combine(plugin, "https-cert.txt")) && File.Exists(Path.Combine(plugin, "index.html")));
        TlsCertificate.RemoveOldFiles(Path.Combine(dir, "does-not-exist"));
        Check("a missing folder is no problem", true);

        // a real TLS 1.2 handshake with it (the .NET Framework side, not the game's)
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool serverOk = false, clientOk = false;
        var t = new Thread(() =>
        {
            try
            {
                using (var cl = listener.AcceptTcpClient())
                using (var ssl = new SslStream(cl.GetStream(), false))
                {
                    ssl.AuthenticateAsServer(c, false, SslProtocols.Tls12, false);
                    serverOk = ssl.IsEncrypted && ssl.IsAuthenticated;
                    ssl.WriteByte(42); ssl.Flush();
                    Thread.Sleep(200);
                }
            }
            catch (Exception ex) { Console.WriteLine("  server: " + ex.Message); }
        });
        t.Start();
        try
        {
            using (var cl = new TcpClient("127.0.0.1", port))
            using (var ssl = new SslStream(cl.GetStream(), false, (s, cert, chain, errors) => true))
            {
                ssl.AuthenticateAsClient("anything");
                clientOk = ssl.ReadByte() == 42 && ssl.SslProtocol == SslProtocols.Tls12;
            }
        }
        catch (Exception ex) { Console.WriteLine("  client: " + ex.Message); }
        t.Join(5000);
        listener.Stop();
        Check("a TLS 1.2 handshake works with the certificate", serverOk && clientOk);
    }
}
