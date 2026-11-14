using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NOTacMap;

// Stand-ins for the game-side pieces MapServer touches.
namespace NOTacMap
{
    internal class FakeLog
    {
        public readonly List<string> Lines = new List<string>();
        public void LogWarning(string m) { lock (Lines) Lines.Add("WARN " + m); }
        public void LogInfo(string m) { lock (Lines) Lines.Add("INFO " + m); }
        public void LogDebug(string m) { lock (Lines) Lines.Add("DEBUG " + m); }
    }
    internal static class Plugin
    {
        public static FakeLog Log = new FakeLog();
        public static string ThemeJson = null;
        public const string PluginVersion = "1.3.0";
        public const string BuildLabel = "";
        public const string TestedGameVersion = "0.34.6";
        public static string GameVersion = "0.35.1";
    }
    internal static class UnitIconCapture
    {
        public class Icon { public byte[] Png; public bool Orient; public float Size; public string Sprite; }
        public static Icon Ready = null;   // what /uniticon answers for the type "Ready"
        public static string Without = "";  // a type that has no usable icon
        public static int Lookup(string key, out Icon icon)
        {
            icon = null;
            if (key == "Ready" && Ready != null) { icon = Ready; return 1; }
            return key == Without ? 2 : 0;
        }
    }
    internal static class MapImageCapture
    {
        public static byte[] Png = null;
        public static byte[] WaterPng = null;
        public static float MapSizeX = 0, MapSizeY = 0;
        public static string MapName = "";
        public static int OffsetX = 80000, OffsetY = 80000;
    }
}

internal sealed class Probe : IDisposable
{
    private readonly Action onDispose;
    public Probe(Action onDispose) { this.onDispose = onDispose; }
    public void Dispose() { onDispose(); }
}

internal static class Program
{
    static int pass, fail;
    static void Check(string name, bool ok) { if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + name); } }

    static HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };

    static async Task<HttpResponseMessage> Send(string method, string url, string host = null, string origin = null, string contentType = null, string body = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (host != null) req.Headers.Host = host;
        if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
        if (body != null) req.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType ?? "text/plain");
        return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    }


    static async Task<bool> WaitFor(Func<bool> condition, int ms = 6000)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    static X509Certificate2 NewCertificate()
    {
        using (var rsa = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=NOTacMap test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            return new X509Certificate2(made.Export(X509ContentType.Pfx, "x"), "x");
        }
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // A plain echo server standing in for the web server behind the front.
    static System.Net.Sockets.TcpListener StartEcho(int port)
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        l.Start();
        new Thread(() =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { return; }
                new Thread(() =>
                {
                    try
                    {
                        var s = c.GetStream(); var b = new byte[4096]; int n;
                        while ((n = s.Read(b, 0, b.Length)) > 0) { s.Write(b, 0, n); s.Flush(); }
                    }
                    catch { }
                    try { c.Close(); } catch { }
                }) { IsBackground = true }.Start();
            }
        }) { IsBackground = true }.Start();
        return l;
    }

    // True when the other side has closed the connection (a read returns 0 or fails) within the time. False when it is still open.
    static bool ClosedWithin(Stream stream, int ms)
    {
        var done = new ManualResetEvent(false);
        bool closed = false;
        var t = new Thread(() =>
        {
            try { var b = new byte[16]; closed = stream.Read(b, 0, 1) <= 0; }
            catch { closed = true; }
            done.Set();
        }) { IsBackground = true };
        t.Start();
        return done.WaitOne(ms) && closed;
    }

    static System.Net.Security.SslStream TlsClient(System.Net.Sockets.TcpClient c)
    {
        var ssl = new System.Net.Security.SslStream(c.GetStream(), false, (s, cert, chain, errors) => true);
        ssl.AuthenticateAsClient("test");
        return ssl;
    }

    // Stands in for the web server where a real one cannot listen (HttpListener needs administrator rights for the network address): it takes
    // the request head, asks FrontPeers who it really is and what the guard says, and answers with the guard's status.
    static System.Net.Sockets.TcpListener StartGuardBackend(IPAddress address, int port, Func<RequestGuard> guard)
    {
        var l = new System.Net.Sockets.TcpListener(address, port);
        l.Start();
        new Thread(() =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient c;
                try { c = l.AcceptTcpClient(); } catch { return; }
                new Thread(() =>
                {
                    try
                    {
                        var s = c.GetStream();
                        var head = new System.Text.StringBuilder(); int b;
                        while (!head.ToString().EndsWith("\r\n\r\n") && (b = s.ReadByte()) >= 0) head.Append((char)b);
                        string[] lines = head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                        string[] first = lines[0].Split(' ');
                        string host = null, cookie = null;
                        foreach (string line in lines) { if (line.StartsWith("Host: ")) host = line.Substring(6); if (line.StartsWith("Cookie: ")) cookie = line.Substring(8); }
                        var remote = (IPEndPoint)c.Client.RemoteEndPoint;
                        string path = first[1]; string query = "";
                        int q = path.IndexOf('?'); if (q >= 0) { query = path.Substring(q); path = path.Substring(0, q); }
                        GuardDecision d = guard().Evaluate(host, null, FrontPeers.Resolve(remote), first[0], path, query, cookie, null, FrontPeers.IsFronted(remote));
                        string reply = "HTTP/1.1 " + (d.Status == 0 ? 200 : d.Status) + " X\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
                        var bytes = System.Text.Encoding.ASCII.GetBytes(reply);
                        s.Write(bytes, 0, bytes.Length); s.Flush();
                    }
                    catch { }
                    try { c.Close(); } catch { }
                }) { IsBackground = true }.Start();
            }
        }) { IsBackground = true }.Start();
        return l;
    }

    // One request over TLS to the front, from this machine's own network address. Returns the status of the answer, or -1.
    static int AskThroughFront(IPAddress from, IPAddress to, int port, string host, string target, string cookie = null)
    {
        try
        {
            using (var c = new System.Net.Sockets.TcpClient(new IPEndPoint(from, 0)))
            {
                c.Connect(to, port);
                var ssl = TlsClient(c);
                string req = "GET " + target + " HTTP/1.1\r\nHost: " + host + "\r\n" + (cookie != null ? "Cookie: " + cookie + "\r\n" : "") + "Connection: close\r\n\r\n";
                var bytes = System.Text.Encoding.ASCII.GetBytes(req);
                ssl.Write(bytes, 0, bytes.Length); ssl.Flush();
                var line = new System.Text.StringBuilder(); int b;
                while ((b = ssl.ReadByte()) >= 0 && b != '\n') line.Append((char)b);
                return int.Parse(line.ToString().Split(' ')[1]);
            }
        }
        catch { return -1; }
    }

    // The same without TLS and without the front: straight to the web server.
    static int AskDirect(IPAddress from, IPAddress to, int port, string host, string target, string cookie = null)
    {
        try
        {
            using (var c = new System.Net.Sockets.TcpClient(new IPEndPoint(from, 0)))
            {
                c.Connect(to, port);
                var s = c.GetStream();
                string req = "GET " + target + " HTTP/1.1\r\nHost: " + host + "\r\n" + (cookie != null ? "Cookie: " + cookie + "\r\n" : "") + "Connection: close\r\n\r\n";
                var bytes = System.Text.Encoding.ASCII.GetBytes(req);
                s.Write(bytes, 0, bytes.Length); s.Flush();
                var line = new System.Text.StringBuilder(); int b;
                while ((b = s.ReadByte()) >= 0 && b != '\n') line.Append((char)b);
                return int.Parse(line.ToString().Split(' ')[1]);
            }
        }
        catch { return -1; }
    }

    static void Main() { Run().GetAwaiter().GetResult(); }

    static async Task Run()
    {
        // The server reads index.html and writes settings.json next to its own assembly.
        string dir = Path.GetDirectoryName(typeof(MapServer).Assembly.Location);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html>test page</html>");
        string settingsFile = Path.Combine(dir, "settings.json");
        if (File.Exists(settingsFile)) File.Delete(settingsFile);

        const int port = 18123;
        string lanIp;
        string why;
        lanIp = LanAddress.Resolve("", out why);
        Console.WriteLine("this machine's LAN address: " + (lanIp ?? "none (" + why + ")"));

        // ===== A. LAN off: the map works on this PC, other things stay shut
        var off = new MapServer(port, null, null);
        off.Start();
        string B = "http://127.0.0.1:" + port;
        var r = await Send("GET", B + "/");
        Check("A page 200", r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("test page"));
        r = await Send("GET", "http://localhost:" + port + "/");
        Check("A page via localhost", r.StatusCode == HttpStatusCode.OK);
        r = await Send("GET", B + "/lan");
        Check("A /lan says disabled", r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()) == "{\"enabled\":false}");
        r = await Send("GET", B + "/", host: "evil.com");
        Check("A spoofed host refused", (int)r.StatusCode == 400 || (int)r.StatusCode == 403);
        r = await Send("POST", B + "/settings", origin: "http://evil.com", contentType: "application/json", body: "{}");
        Check("A foreign origin 403", (int)r.StatusCode == 403);
        r = await Send("POST", B + "/settings", contentType: "text/plain", body: "{}");
        Check("A wrong content type 415", (int)r.StatusCode == 415);
        r = await Send("POST", B + "/settings", contentType: "application/json", body: "not json");
        Check("A bad json 400", (int)r.StatusCode == 400);
        r = await Send("POST", B + "/settings", origin: "http://127.0.0.1:" + port, contentType: "application/json", body: "{\"a\":1}");
        Check("A valid save 204", (int)r.StatusCode == 204 && File.ReadAllText(settingsFile) == "{\"a\":1}");
        r = await Send("GET", B + "/settings");
        Check("A settings read back", (await r.Content.ReadAsStringAsync()) == "{\"a\":1}");
        r = await Send("DELETE", B + "/");
        Check("A DELETE 405", (int)r.StatusCode == 405);
        r = await Send("GET", B + "/theme");
        Check("A theme 404 before capture", (int)r.StatusCode == 404);
        r = await Send("GET", B + "/nope");
        Check("A unknown 404", (int)r.StatusCode == 404);

        // event stream still works and sends a first comment at once
        var ev = await Send("GET", B + "/events");
        Check("A events 200 + event-stream", ev.StatusCode == HttpStatusCode.OK && ev.Content.Headers.ContentType.MediaType == "text/event-stream");
        var stream = await ev.Content.ReadAsStreamAsync();
        var buf = new byte[64];
        var read = await stream.ReadAsync(buf, 0, buf.Length);
        Check("A events first bytes are a comment", System.Text.Encoding.UTF8.GetString(buf, 0, read).StartsWith(":"));
        off.Broadcast("{\"x\":1}");
        read = await stream.ReadAsync(buf, 0, buf.Length);
        Check("A broadcast arrives", System.Text.Encoding.UTF8.GetString(buf, 0, read).Contains("data: {\"x\":1}"));
        ev.Dispose();
        off.Stop();
        Thread.Sleep(300);

        // ===== B. LAN asked for. Without admin rights Windows refuses the address, so this
        // checks the fallback; with admin it checks the opposite (listener up, URL built).
        if (lanIp != null)
        {
            Plugin.Log.Lines.Clear();
            var on = new MapServer(port, lanIp, "0123456789abcdef0123456789abcdef");
            on.Start();
            bool fellBack = !on.LanActive;
            Console.WriteLine("LAN listener " + (fellBack ? "was refused by Windows -> fell back to this PC only" : "started on " + lanIp));
            r = await Send("GET", B + "/");
            Check("B map still works on this PC either way", r.StatusCode == HttpStatusCode.OK);
            r = await Send("GET", B + "/lan");
            string lanJson = await r.Content.ReadAsStringAsync();
            if (fellBack)
            {
                Check("B fallback: /lan says disabled", lanJson == "{\"enabled\":false}");
                Check("B fallback: a warning was logged with the reason", Plugin.Log.Lines.Exists(l => l.StartsWith("WARN") && l.Contains("LAN access is off")));
                Check("B fallback: no link or token in the log", !Plugin.Log.Lines.Exists(l => l.Contains("0123456789abcdef")));
                Check("B fallback: LanUrl is null", on.LanUrl == null);
            }
            else
            {
                Check("B /lan has the link", lanJson.Contains("http://" + lanIp + ":" + port + "/?t=0123456789abcdef0123456789abcdef"));
                Check("B no token in the log", !Plugin.Log.Lines.Exists(l => l.Contains("0123456789abcdef")));
                // from this PC but using the LAN address: reaches the server, loopback or not depending on routing
                r = await Send("GET", "http://" + lanIp + ":" + port + "/");
                Console.WriteLine("(request to the LAN address came back as " + (int)r.StatusCode + ")");
            }
            on.Stop();
        }

        // ===== C. The address changes while the server runs
        {
            const string tok = "0123456789abcdef0123456789abcdef";
            Plugin.Log.Lines.Clear();
            var c = new MapServer(port, null, tok);
            c.Start();
            r = await Send("GET", B + "/lan");
            Check("C starts with no LAN address", (await r.Content.ReadAsStringAsync()) == "{\"enabled\":false}" && !c.LanActive);

            // a streamC that is open when the listener is rebuilt must end, and a new one must work afterwards
            var evC = await client.GetAsync(B + "/events", HttpCompletionOption.ResponseHeadersRead);
            var streamC = await evC.Content.ReadAsStreamAsync();
            var bufC = new byte[256];
            await streamC.ReadAsync(bufC, 0, bufC.Length);

            c.SetLanAddress(lanIp);
            Console.WriteLine("C: LAN address " + (c.LanActive ? "accepted" : "refused by Windows, fell back to this PC only"));
            r = await Send("GET", B + "/");
            Check("C map still answers after the rebuild", r.StatusCode == HttpStatusCode.OK);
            c.Broadcast("{\"z\":3}");
            int nz = await streamC.ReadAsync(bufC, 0, bufC.Length);
            Check("C the PC's event stream stays open while the LAN listener changes", nz > 0 && System.Text.Encoding.UTF8.GetString(bufC, 0, nz).Contains("{\"z\":3}"));
            var evC2 = await client.GetAsync(B + "/events", HttpCompletionOption.ResponseHeadersRead);
            Check("C a new event streamC works", evC2.StatusCode == HttpStatusCode.OK);
            var streamC2 = await evC2.Content.ReadAsStreamAsync();
            await streamC2.ReadAsync(bufC, 0, bufC.Length);
            c.Broadcast("{\"y\":2}");
            int n = await streamC2.ReadAsync(bufC, 0, bufC.Length);
            Check("C a broadcast reaches the new streamC", System.Text.Encoding.UTF8.GetString(bufC, 0, n).Contains("{\"y\":2}"));
            evC2.Dispose();

            r = await Send("GET", B + "/lan");
            string json = await r.Content.ReadAsStringAsync();
            if (c.LanActive) Check("C /lan has the new link", json.Contains("http://" + lanIp + ":" + port + "/?t=" + tok));
            else
            {
                Check("C refused address: /lan stays disabled", json == "{\"enabled\":false}");
                int warnsBefore = Plugin.Log.Lines.FindAll(l => l.StartsWith("WARN")).Count;
                c.SetLanAddress(lanIp);
                c.SetLanAddress(lanIp);
                Check("C a refused address is not retried", Plugin.Log.Lines.FindAll(l => l.StartsWith("WARN")).Count == warnsBefore);
            }
            Check("C no token in the log", !Plugin.Log.Lines.Exists(l => l.Contains(tok)));

            c.SetLanAddress(null);
            r = await Send("GET", B + "/");
            Check("C back to no address, map answers", r.StatusCode == HttpStatusCode.OK && !c.LanActive);
            r = await Send("GET", B + "/lan");
            Check("C /lan disabled again", (await r.Content.ReadAsStringAsync()) == "{\"enabled\":false}");
            c.SetLanAddress(null); // same value twice does nothing
            r = await Send("GET", B + "/");
            Check("C setting the same address again is harmless", r.StatusCode == HttpStatusCode.OK);
            c.Stop();
            Thread.Sleep(300);

            // a server that was started without LAN ignores address changes
            var plain = new MapServer(port, null, null);
            plain.Start();
            plain.SetLanAddress(lanIp);
            Check("C a PC-only server ignores address changes", !plain.LanActive);
            r = await Send("GET", B + "/");
            Check("C PC-only server still answers", r.StatusCode == HttpStatusCode.OK);
            plain.Stop();
        }

        // ===== E. The LAN switch: POST /lan from this PC, EnableLan and DisableLan
        {
            const string tok = "0123456789abcdef0123456789abcdef";
            Plugin.Log.Lines.Clear();
            var e = new MapServer(port, null, null);
            var calls = new List<bool>();
            string answer = null;
            e.LanSwitch = on => { calls.Add(on); return answer; };
            e.Start();

            Func<string, string, Task<HttpResponseMessage>> post = (ct, body) =>
                Send("POST", B + "/lan", null, null, ct, body);

            r = await post("application/json", "{\"enabled\":true}");
            string txt = await r.Content.ReadAsStringAsync();
            Check("E POST /lan true: 200, handler called with true", r.StatusCode == HttpStatusCode.OK && calls.Count == 1 && calls[0] == true);
            Check("E POST /lan answers with the state (still off here)", txt == "{\"enabled\":false}");
            r = await post("application/json", "{\"enabled\":false}");
            Check("E POST /lan false: handler called with false", r.StatusCode == HttpStatusCode.OK && calls.Count == 2 && calls[1] == false);

            answer = "No private network address found on this PC.";
            r = await post("application/json", "{\"enabled\":true}");
            txt = await r.Content.ReadAsStringAsync();
            Check("E a failure comes back as an error message", txt.Contains("\"error\":\"No private network address found on this PC.\""));
            answer = "say \"hi\" \\ and a\nnewline";
            r = await post("application/json", "{\"enabled\":true}");
            txt = await r.Content.ReadAsStringAsync();
            Check("E the error message is escaped as JSON", Newtonsoft.Json.Linq.JObject.Parse(txt)["error"].ToString() == answer);
            answer = null;

            int before = calls.Count;
            r = await post("text/plain", "{\"enabled\":true}");
            Check("E wrong content type 415", (int)r.StatusCode == 415);
            r = await post("application/json", "not json");
            Check("E bad json 400", (int)r.StatusCode == 400);
            r = await post("application/json", "{\"on\":true}");
            Check("E missing field 400", (int)r.StatusCode == 400);
            r = await post("application/json", "{\"enabled\":\"yes\"}");
            Check("E field of the wrong type 400", (int)r.StatusCode == 400);
            r = await post("application/json", "{\"enabled\":1}");
            Check("E a number is not a boolean 400", (int)r.StatusCode == 400);
            r = await post("application/json", "[true]");
            Check("E an array 400", (int)r.StatusCode == 400);
            r = await post("application/json", "{\"enabled\":true,\"pad\":\"" + new string('x', 3000) + "\"}");
            Check("E too large 413", (int)r.StatusCode == 413);
            r = await Send("POST", B + "/lan", null, "http://evil.com", "application/json", "{\"enabled\":true}");
            Check("E cross-site origin 403", (int)r.StatusCode == 403);
            r = await Send("POST", B + "/lan", "evil.com:" + port, null, "application/json", "{\"enabled\":true}");
            Check("E rebinding host refused", (int)r.StatusCode == 400 || (int)r.StatusCode == 403);
            r = await Send("PUT", B + "/lan", null, null, "application/json", "{\"enabled\":true}");
            Check("E PUT 405", (int)r.StatusCode == 405);
            Check("E none of the refused requests reached the handler", calls.Count == before);

            r = await Send("GET", B + "/lan");
            Check("E GET /lan unchanged", (await r.Content.ReadAsStringAsync()) == "{\"enabled\":false}");

            // no handler wired: a clear message, no crash
            var bare = new MapServer(port + 1, null, null);
            bare.Start();
            r = await Send("POST", "http://127.0.0.1:" + (port + 1) + "/lan", null, null, "application/json", "{\"enabled\":true}");
            Check("E no handler: answers with an error", r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("\"error\""));
            bare.Stop();

            // EnableLan / DisableLan on the server itself
            Check("E starts with LAN not allowed", !e.LanAllowed && !e.LanActive);
            e.EnableLan(tok, lanIp);
            Check("E EnableLan marks LAN as allowed", e.LanAllowed);
            Console.WriteLine("E: LAN address " + (e.LanActive ? "accepted" : "refused by Windows, so LanActive stays false"));
            r = await Send("GET", B + "/");
            Check("E map still answers after EnableLan", r.StatusCode == HttpStatusCode.OK);
            r = await Send("GET", B + "/lan");
            string state = await r.Content.ReadAsStringAsync();
            if (e.LanActive) Check("E /lan has the link after EnableLan", state.Contains("http://" + lanIp + ":" + port + "/?t=" + tok));
            else Check("E refused address: /lan stays disabled", state == "{\"enabled\":false}");
            Check("E no token in the log", !Plugin.Log.Lines.Exists(l => l.Contains(tok)));

            e.DisableLan();
            Check("E DisableLan: not allowed, not active", !e.LanAllowed && !e.LanActive);
            r = await Send("GET", B + "/");
            Check("E map answers after DisableLan", r.StatusCode == HttpStatusCode.OK);
            r = await Send("GET", B + "/lan");
            Check("E /lan disabled after DisableLan", (await r.Content.ReadAsStringAsync()) == "{\"enabled\":false}");

            // turning it on again retries an address that was refused before
            Plugin.Log.Lines.Clear();
            e.EnableLan(tok, lanIp);
            if (!e.LanActive) Check("E a second EnableLan tries the address again", Plugin.Log.Lines.Exists(l => l.StartsWith("WARN") && l.Contains("LAN access is off")));
            e.DisableLan();

            // a network change while LAN is switched off must not turn it on
            e.SetLanAddress(lanIp);
            Check("E SetLanAddress does nothing while LAN is off", !e.LanActive && !e.LanAllowed);
            e.Stop();
            Thread.Sleep(300);
        }

        // ===== F. The network listener for real, using a loopback alias as a stand-in address
        {
            const string tok = "0123456789abcdef0123456789abcdef";
            Plugin.Log.Lines.Clear();
            var f = new MapServer(port, null, null);
            f.Start();
            var evF = await client.GetAsync(B + "/events", HttpCompletionOption.ResponseHeadersRead);
            var streamF = await evF.Content.ReadAsStreamAsync();
            var bufF = new byte[256];
            await streamF.ReadAsync(bufF, 0, bufF.Length);

            f.EnableLan(tok, "127.0.0.2");
            Console.WriteLine("F: 127.0.0.2 as a stand-in LAN address " + (f.LanActive ? "accepted" : "refused by Windows, F skipped"));
            if (f.LanActive)
            {
                r = await Send("GET", "http://127.0.0.2:" + port + "/");
                Check("F the page answers on the network listener", r.StatusCode == HttpStatusCode.OK);
                r = await Send("GET", B + "/lan");
                Check("F /lan has the link", (await r.Content.ReadAsStringAsync()).Contains("http://127.0.0.2:" + port + "/?t=" + tok));
                f.Broadcast("{\"f\":1}");
                int nf = await streamF.ReadAsync(bufF, 0, bufF.Length);
                Check("F the PC's stream is untouched by switching LAN on", nf > 0 && System.Text.Encoding.UTF8.GetString(bufF, 0, nf).Contains("{\"f\":1}"));

                // moving the address: the old one stops answering, the PC never notices
                f.SetLanAddress("127.0.0.3");
                bool oldGone;
                try { oldGone = (await client.GetAsync("http://127.0.0.2:" + port + "/")).StatusCode != HttpStatusCode.OK; } catch (Exception) { oldGone = true; }
                Check("F the old network address stops answering after a move", oldGone);
                r = await Send("GET", "http://127.0.0.3:" + port + "/");
                Check("F the new network address answers", f.LanActive && r.StatusCode == HttpStatusCode.OK);
                r = await Send("GET", B + "/");
                Check("F the PC page answers after the move", r.StatusCode == HttpStatusCode.OK);
                f.Broadcast("{\"f\":2}");
                nf = await streamF.ReadAsync(bufF, 0, bufF.Length);
                Check("F the PC's stream survived the move", nf > 0 && System.Text.Encoding.UTF8.GetString(bufF, 0, nf).Contains("{\"f\":2}"));

                f.DisableLan();
                try { oldGone = (await client.GetAsync("http://127.0.0.3:" + port + "/")).StatusCode != HttpStatusCode.OK; } catch (Exception) { oldGone = true; }
                Check("F switching LAN off closes the network listener", oldGone && !f.LanActive && !f.LanAllowed);
                r = await Send("GET", B + "/");
                Check("F the PC page still answers after switching LAN off", r.StatusCode == HttpStatusCode.OK);
                f.Broadcast("{\"f\":3}");
                nf = await streamF.ReadAsync(bufF, 0, bufF.Length);
                Check("F the PC's stream survived switching LAN off", nf > 0 && System.Text.Encoding.UTF8.GetString(bufF, 0, nf).Contains("{\"f\":3}"));
                f.EnableLan(tok, "127.0.0.2");
                r = await Send("GET", "http://127.0.0.2:" + port + "/");
                Check("F switching LAN on again works", f.LanActive && r.StatusCode == HttpStatusCode.OK);
            }
            evF.Dispose();
            f.Stop();
            Thread.Sleep(300);
        }

        // ===== J. the HTTPS front (TlsFront) in front of a real MapServer, over real TLS
        {
            const string tok = "0123456789abcdef0123456789abcdef";
            Plugin.Log.Lines.Clear();
            int httpsPort = port + 7;
            System.Net.ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            X509Certificate2 cert;
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest("CN=NOTacMap test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
                cert = new X509Certificate2(made.Export(X509ContentType.Pfx, "x"), "x");
            }
            var jc = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
            jc.DefaultRequestHeaders.ConnectionClose = true;
            // The test host's HttpListener (HTTP.sys) matches the Host port, the game's does not, so the plain port is named here.
            Func<string, string, string, Task<HttpResponseMessage>> viaHttps = (method, path, body) =>
            {
                var req = new HttpRequestMessage(new HttpMethod(method), "https://127.0.0.2:" + httpsPort + path);
                req.Headers.Host = "127.0.0.2:" + port;
                if (body != null) req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                return jc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            };

            var j = new MapServer(port, null, null);
            j.ConfigureHttps(true, httpsPort);
            int madeCalls = 0;
            j.HttpsFrontStart = (address, frontPort, backendPort) => { Interlocked.Increment(ref madeCalls); return TlsFront.Start(address, frontPort, backendPort, cert); };
            var reached = new List<bool>();
            j.HttpsReached = on => { lock (reached) reached.Add(on); };
            j.HttpsFingerprint = "AA:BB:CC";
            j.Start();
            j.EnableLan(tok, "127.0.0.2");
            Console.WriteLine("J: 127.0.0.2 as a stand-in LAN address " + (j.LanActive ? "accepted" : "refused by Windows, J skipped"));
            if (j.LanActive)
            {
                Check("J HTTPS comes up on its own thread and then the link is https", await WaitFor(() => j.HttpsActive) && j.LanUrl.StartsWith("https://127.0.0.2:" + httpsPort + "/?t="));
                Check("J the plugin was told the state was reached", await WaitFor(() => { lock (reached) return reached.Contains(true); }));
                r = await Send("GET", B + "/lan");
                string lanJson = await r.Content.ReadAsStringAsync();
                Check("J /lan says https is on, wanted, and its port", lanJson.Contains("\"https\":true") && lanJson.Contains("\"httpsWanted\":true") && lanJson.Contains("\"httpsPort\":" + httpsPort));
                Check("J /lan carries the certificate fingerprint for this PC", lanJson.Contains("\"certSha256\":\"AA:BB:CC\"") && lanJson.Contains("\"httpsStarting\":false") && lanJson.Contains("\"httpsOnly\":false"));
                r = await viaHttps("GET", "/about", null);
                Check("J a page comes through TLS: 200", r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("\"version\""));
                r = await viaHttps("POST", "/lan", "{\"https\":true}");
                Check("J a POST body comes through TLS", r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("can't be switched"));
                r = await viaHttps("GET", "/nothing-here", null);
                Check("J a missing page is a 404 through TLS", r.StatusCode == HttpStatusCode.NotFound);

                var evJ = await viaHttps("GET", "/events", null);
                var streamJ = await evJ.Content.ReadAsStreamAsync();
                var bufJ = new byte[256];
                await streamJ.ReadAsync(bufJ, 0, bufJ.Length);
                Check("J the event stream opens over TLS", evJ.StatusCode == HttpStatusCode.OK);
                j.Broadcast("{\"j\":1}");
                int nj = await streamJ.ReadAsync(bufJ, 0, bufJ.Length);
                Check("J an event arrives over TLS as it is sent", nj > 0 && System.Text.Encoding.UTF8.GetString(bufJ, 0, nj).Contains("{\"j\":1}"));
                Check("J while the stream is open the front lists its connection", FrontPeers.Count >= 1);
                evJ.Dispose();
                for (int i = 0; i < 40 && FrontPeers.Count > 0; i++) Thread.Sleep(100);
                Check("J when the connection is gone the list is empty again", FrontPeers.Count == 0);

                j.SetHttps(false);
                Check("J off: the link is plain again", !j.HttpsActive && j.LanUrl.StartsWith("http://127.0.0.2:" + port + "/?t="));
                Check("J off: the plugin was told it is off", await WaitFor(() => { lock (reached) return reached.Contains(false); }));
                bool secureGone;
                try { await viaHttps("GET", "/about", null); secureGone = false; } catch (Exception) { secureGone = true; }
                Check("J off: the https port stops answering", secureGone);
                r = await Send("GET", "http://127.0.0.2:" + port + "/about");
                Check("J off: the plain address still answers", r.StatusCode == HttpStatusCode.OK);
                Check("J off: a wanted-but-off state is shown", (await (await Send("GET", B + "/lan")).Content.ReadAsStringAsync()).Contains("\"https\":false"));

                j.SetHttps(true);
                Check("J on again: it starts", await WaitFor(() => j.HttpsActive));
                r = await viaHttps("GET", "/about", null);
                Check("J on again: a page comes through", r.StatusCode == HttpStatusCode.OK);

                // switching LAN off takes the front and its open connections with it, and on again brings it back (HTTPS was still wanted)
                var evOff = await viaHttps("GET", "/events", null);
                var offStream = await evOff.Content.ReadAsStreamAsync();
                var offBuf = new byte[64]; await offStream.ReadAsync(offBuf, 0, offBuf.Length);
                j.DisableLan();
                Check("J LAN off: an open stream through the front is closed", ClosedWithin(offStream, 4000));
                evOff.Dispose();
                Check("J LAN off: no front is left", !j.HttpsActive);
                secureGone = false;
                try { await viaHttps("GET", "/about", null); } catch (Exception) { secureGone = true; }
                Check("J LAN off: the https port stops answering", secureGone);
                j.EnableLan(tok, "127.0.0.2");
                Check("J LAN on again: the front is back", await WaitFor(() => j.HttpsActive));

                // a new link closes everything that came through the front, and the front starts again
                var evLink = await viaHttps("GET", "/events", null);
                var linkStream = await evLink.Content.ReadAsStreamAsync();
                var linkBuf = new byte[64]; await linkStream.ReadAsync(linkBuf, 0, linkBuf.Length);
                int madeBefore = madeCalls;
                j.EnableLan("fedcba9876543210fedcba9876543210", "127.0.0.2");
                Check("J a new link: an open stream through the front is closed", ClosedWithin(linkStream, 4000));
                evLink.Dispose();
                Check("J a new link: the front starts again", await WaitFor(() => j.HttpsActive && madeCalls > madeBefore));
                r = await viaHttps("GET", "/about", null);
                Check("J a new link: the https link answers again", r.StatusCode == HttpStatusCode.OK);

                // moving the address closes the connections of the old front and starts a new one
                var evMove = await viaHttps("GET", "/events", null);
                var moveStream = await evMove.Content.ReadAsStreamAsync();
                var moveBuf = new byte[64]; await moveStream.ReadAsync(moveBuf, 0, moveBuf.Length);
                j.SetLanAddress("127.0.0.3");
                Check("J the address moves: an open stream through the old front is closed", ClosedWithin(moveStream, 4000));
                evMove.Dispose();
                Check("J the address moves: the https link follows", await WaitFor(() => j.HttpsActive && j.LanUrl.StartsWith("https://127.0.0.3:")));
            }
            // a front that cannot start leaves the plain link working, and the config is never told it is on
            var bad = new MapServer(port + 8, null, null);
            bad.ConfigureHttps(true, port + 9);
            var badReached = new List<bool>();
            bad.HttpsReached = on => { lock (badReached) badReached.Add(on); };
            bad.HttpsFrontStart = (address, frontPort, backendPort) => { throw new InvalidOperationException("no certificate"); };
            bad.Start();
            bad.EnableLan(tok, "127.0.0.2");
            if (bad.LanActive)
            {
                await Task.Delay(800);
                Check("J a front that cannot start: HTTPS stays off, the link stays plain", !bad.HttpsActive && bad.LanUrl.StartsWith("http://"));
                Check("J a front that cannot start: the plugin is never told it is on", badReached.Count == 0);
                string badJson = await (await Send("GET", "http://127.0.0.1:" + (port + 8) + "/lan")).Content.ReadAsStringAsync();
                Check("J a front that cannot start: /lan says why and that the wish is gone", badJson.Contains("\"httpsError\":\"HTTPS could not start") && badJson.Contains("\"httpsWanted\":false") && badJson.Contains("\"httpsStarting\":false"));
                Check("J a front that cannot start: it is said in the log", Plugin.Log.Lines.Exists(l => l.Contains("HTTPS could not start")));
                r = await Send("GET", "http://127.0.0.2:" + (port + 8) + "/about");
                Check("J a front that cannot start: the plain address still answers", r.StatusCode == HttpStatusCode.OK);
            }
            bad.Stop();

            // a start that is overtaken is thrown away: switched off while it was still being made
            var slow = new MapServer(port + 10, null, null);
            slow.ConfigureHttps(true, port + 11);
            var slowReached = new List<bool>();
            slow.HttpsReached = on => { lock (slowReached) slowReached.Add(on); };
            int slowDisposed = 0;
            var gate = new ManualResetEvent(false);
            slow.HttpsFrontStart = (address, frontPort, backendPort) => { gate.WaitOne(5000); return new Probe(() => Interlocked.Increment(ref slowDisposed)); };
            slow.Start();
            slow.EnableLan(tok, "127.0.0.2");
            if (slow.LanActive)
            {
                string startingJson = await (await Send("GET", "http://127.0.0.1:" + (port + 10) + "/lan")).Content.ReadAsStringAsync();
                Check("J while the certificate is being made /lan says starting, and the link is still http", startingJson.Contains("\"httpsStarting\":true") && !slow.HttpsActive && slow.LanUrl.StartsWith("http://"));
                slow.SetHttps(false);
                gate.Set();
                Check("J a start that was switched off meanwhile is thrown away", await WaitFor(() => slowDisposed == 1) && !slow.HttpsActive);
                lock (slowReached) Check("J and it is only reported as off, never as on", !slowReached.Contains(true) && slowReached.Contains(false));

                // overtaken by another address
                gate.Reset();
                slow.SetHttps(true);
                slow.SetLanAddress("127.0.0.3");
                gate.Set();
                Check("J a start for the old address is thrown away after a move", await WaitFor(() => slowDisposed >= 2));
            }
            slow.Stop();
            j.Stop();
            Thread.Sleep(300);
        }

        // ===== K. TlsFront limits, with short deadlines (they are 10 seconds in the game)
        {
            Check("K the peer list starts empty", await WaitFor(() => FrontPeers.Count == 0));
            X509Certificate2 kcert = NewCertificate();
            int back = FreePort(), front = FreePort();
            var echo = StartEcho(back);
            int saveHandshake = TlsFront.HandshakeMs, saveFirst = TlsFront.FirstRequestMs, savePer = TlsFront.MaxPerAddress;
            TlsFront.HandshakeMs = 900; TlsFront.FirstRequestMs = 900; TlsFront.MaxPerAddress = 3;
            var kf = TlsFront.Start("127.0.0.1", front, back, kcert);
            try
            {
                // a connection that sends nothing is dropped by the handshake deadline
                var idle = new System.Net.Sockets.TcpClient("127.0.0.1", front);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool dropped = ClosedWithin(idle.GetStream(), 4000);
                Check("K an idle connection is dropped by the handshake deadline, not kept", dropped && sw.ElapsedMilliseconds < 3500);
                idle.Close();

                // one byte at a time does not get past the total deadline (a read timeout alone would let it hold the slot for ever)
                var drip = new System.Net.Sockets.TcpClient("127.0.0.1", front);
                var dripStream = drip.GetStream();
                bool dripDropped = false;
                sw.Restart();
                // the head of a handshake record that is 16384 bytes long, then its body one byte at a time
                dripStream.Write(new byte[] { 0x16, 0x03, 0x01, 0x40, 0x00 }, 0, 5); dripStream.Flush();
                for (int i = 0; i < 30 && !dripDropped; i++)
                {
                    try { dripStream.WriteByte(0x01); dripStream.Flush(); } catch { dripDropped = true; break; }
                    Thread.Sleep(200);
                    if (sw.ElapsedMilliseconds > 1200 && ClosedWithin(dripStream, 50)) dripDropped = true;
                }
                Check("K a sender that drips one byte at a time is dropped by the total deadline", dripDropped && sw.ElapsedMilliseconds < 5000);
                drip.Close();

                // a finished handshake with no request is dropped by the first request deadline
                var quiet = new System.Net.Sockets.TcpClient("127.0.0.1", front);
                var quietSsl = TlsClient(quiet);
                sw.Restart();
                Check("K a connection that says nothing after the handshake is dropped", ClosedWithin(quietSsl, 4000) && sw.ElapsedMilliseconds < 3500);
                quiet.Close();

                // once the first bytes are there there is no reading deadline (the live stream is long lived), and the bytes go to the server and back
                var busy = new System.Net.Sockets.TcpClient("127.0.0.1", front);
                var busySsl = TlsClient(busy);
                busySsl.Write(new byte[] { 65 }, 0, 1); busySsl.Flush();
                Check("K the first bytes go through to the server and come back", busySsl.ReadByte() == 65);
                Check("K while it is open the peer list has its connection", FrontPeers.Count == 1);
                Thread.Sleep(1800);   // longer than the first request deadline
                busySsl.Write(new byte[] { 66 }, 0, 1); busySsl.Flush();
                Check("K after the first bytes there is no reading deadline", busySsl.ReadByte() == 66);

                // the cap for one address (a longer handshake deadline for this part, so the ones that are held stay)
                TlsFront.HandshakeMs = 4000;
                Thread.Sleep(300);
                var held = new List<System.Net.Sockets.TcpClient>();
                for (int i = 0; i < 2; i++) held.Add(new System.Net.Sockets.TcpClient("127.0.0.1", front));   // with busy, three from this address
                var extra = new System.Net.Sockets.TcpClient("127.0.0.1", front);
                Check("K a connection over the cap for one address is refused at once", ClosedWithin(extra.GetStream(), 1500));
                extra.Close();
                bool stillOpen = held.TrueForAll(c => !ClosedWithin(c.GetStream(), 100));
                Check("K the ones under the cap are not touched", stillOpen);
                foreach (var h in held) h.Close();
                busySsl.Dispose(); busy.Close();
                Check("K when they are gone the list is empty again", await WaitFor(() => FrontPeers.Count == 0));

                // the accept loop goes on after an error
                TlsFront.TestFaultOnce = true;
                var after = new System.Net.Sockets.TcpClient("127.0.0.1", front);
                var afterSsl = TlsClient(after);
                afterSsl.Write(new byte[] { 67 }, 0, 1); afterSsl.Flush();
                Check("K the connection before the error is served", afterSsl.ReadByte() == 67);
                Check("K the accept loop met the error", await WaitFor(() => !TlsFront.TestFaultOnce));
                Thread.Sleep(300);
                var after2 = new System.Net.Sockets.TcpClient("127.0.0.1", front);
                var after2Ssl = TlsClient(after2);
                after2Ssl.Write(new byte[] { 68 }, 0, 1); after2Ssl.Flush();
                Check("K an error in the accept loop does not end it", after2Ssl.ReadByte() == 68);

                // Dispose closes what is open
                kf.Dispose();
                Check("K closing the front closes the open connections", ClosedWithin(afterSsl, 3000) && ClosedWithin(after2Ssl, 3000));
                after.Close(); after2.Close();
                Check("K and the peer list is empty", await WaitFor(() => FrontPeers.Count == 0));
                bool refusedNow;
                try { var late = new System.Net.Sockets.TcpClient("127.0.0.1", front); refusedNow = ClosedWithin(late.GetStream(), 1000); late.Close(); } catch (Exception) { refusedNow = true; }
                Check("K a closed front takes no new connection", refusedNow);
            }
            finally
            {
                try { kf.Dispose(); } catch { }
                try { echo.Stop(); } catch { }
                TlsFront.HandshakeMs = saveHandshake; TlsFront.FirstRequestMs = saveFirst; TlsFront.MaxPerAddress = savePer;
            }
        }

        // ===== L. the front, the peer list and the guard together, with a phone address that is not loopback
        {
            const string tok = "0123456789abcdef0123456789abcdef";
            string problem;
            string lanText = LanAddress.Resolve("", out problem);
            Console.WriteLine("L: this machine's network address " + (lanText ?? ("none (" + problem + "), L skipped")));
            if (lanText != null)
            {
                IPAddress lan = IPAddress.Parse(lanText);
                int back = FreePort(), front = FreePort(), plainBack = FreePort();
                string secureHost = lanText + ":" + front;
                var open = new RequestGuard(back, lanText, tok, front, false);
                var only = new RequestGuard(back, lanText, tok, front, true);
                RequestGuard current = open;
                var server = StartGuardBackend(lan, back, () => current);
                var loopServer = StartGuardBackend(IPAddress.Loopback, back, () => current);
                var lf = TlsFront.Start(lanText, front, back, NewCertificate());
                try
                {
                    string cookie = RequestGuard.CookieName + "=" + tok, secureCookie = RequestGuard.SecureCookieName + "=" + tok;
                    Check("L through the front without the secret: refused (401)", AskThroughFront(lan, lan, front, secureHost, "/about") == 401);
                    Check("L through the front with a wrong secret: refused (401)", AskThroughFront(lan, lan, front, secureHost, "/about", RequestGuard.CookieName + "=wrongwrongwrongwrongwrongwrongwr") == 401);
                    Check("L through the front with the link secret: sent on with a cookie (302)", AskThroughFront(lan, lan, front, secureHost, "/about?t=" + tok) == 302);
                    Check("L through the front with the cookie: let in (200)", AskThroughFront(lan, lan, front, secureHost, "/about", cookie) == 200);
                    Check("L through the front with the __Host- cookie: let in (200)", AskThroughFront(lan, lan, front, secureHost, "/about", secureCookie) == 200);
                    Check("L the phone link page is for this PC only, through the front too (403)", AskThroughFront(lan, lan, front, secureHost, "/lan", cookie) == 403);
                    Check("L a direct request on loopback is trusted (200)", AskDirect(IPAddress.Loopback, IPAddress.Loopback, back, "127.0.0.1:" + back, "/about") == 200);
                    Check("L a direct request on the network address, no secret: refused (401)", AskDirect(lan, lan, back, lanText + ":" + back, "/about") == 401);
                    Check("L a direct request on the network address, with the cookie: let in (200)", AskDirect(lan, lan, back, lanText + ":" + back, "/about", cookie) == 200);
                    // a direct request that names the secure host is never taken for this PC, even from loopback
                    Check("L a direct request on loopback that names the secure host: refused (403)", AskDirect(IPAddress.Loopback, IPAddress.Loopback, back, secureHost, "/about") == 403);

                    // HttpsOnly: other devices only through the front, this PC unaffected
                    current = only;
                    Check("L HttpsOnly: through the front with the cookie: let in (200)", AskThroughFront(lan, lan, front, secureHost, "/about", cookie) == 200);
                    Check("L HttpsOnly: a direct request on the network address with the cookie: refused (403)", AskDirect(lan, lan, back, lanText + ":" + back, "/about", cookie) == 403);
                    Check("L HttpsOnly: a direct request on loopback is still trusted (200)", AskDirect(IPAddress.Loopback, IPAddress.Loopback, back, "127.0.0.1:" + back, "/about") == 200);
                }
                finally
                {
                    try { lf.Dispose(); } catch { }
                    try { server.Stop(); loopServer.Stop(); } catch { }
                }
            }
        }

        // ===== G. 1.2.1: Sec-Fetch-Site on /events over real HTTP, and the "new link" request
        {
            Plugin.Log.Lines.Clear();
            var g = new MapServer(port, null, null);
            string newLinkAnswer = null;
            int newLinkCalls = 0;
            g.LanNewLink = () => { newLinkCalls++; return newLinkAnswer; };
            g.Start();

            Func<string, Task<HttpResponseMessage>> events = async site =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, B + "/events");
                if (site != null) req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
                return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            };
            var gr = await events("cross-site");
            Check("G /events with Sec-Fetch-Site cross-site: 403", (int)gr.StatusCode == 403);
            gr.Dispose();
            gr = await events("same-site");
            Check("G /events with Sec-Fetch-Site same-site: 403", (int)gr.StatusCode == 403);
            gr.Dispose();
            gr = await events("same-origin");
            Check("G /events with Sec-Fetch-Site same-origin: 200", gr.StatusCode == HttpStatusCode.OK);
            gr.Dispose();
            gr = await events("none");
            Check("G /events with Sec-Fetch-Site none: 200", gr.StatusCode == HttpStatusCode.OK);
            gr.Dispose();
            gr = await events(null);
            Check("G /events with no header: 200", gr.StatusCode == HttpStatusCode.OK);
            gr.Dispose();
            // the refused requests did not take slots: 8 more streams still fit after a pile of cross-site tries
            for (int i = 0; i < 12; i++) { var x = await events("cross-site"); x.Dispose(); }
            var held = new List<HttpResponseMessage>();
            bool allFit = true;
            for (int i = 0; i < 5; i++) { var x = await events("same-origin"); held.Add(x); if (x.StatusCode != HttpStatusCode.OK) allFit = false; }
            Check("G cross-site tries leave the stream slots free", allFit);
            foreach (var x in held) x.Dispose();

            Func<string, Task<HttpResponseMessage>> postLan = body => Send("POST", B + "/lan", null, null, "application/json", body);
            r = await postLan("{\"newLink\":true}");
            string t = await r.Content.ReadAsStringAsync();
            Check("G newLink: 200 and the handler ran once", r.StatusCode == HttpStatusCode.OK && newLinkCalls == 1 && t == "{\"enabled\":false}");
            newLinkAnswer = "Turn on access from other devices first.";
            r = await postLan("{\"newLink\":true}");
            t = await r.Content.ReadAsStringAsync();
            Check("G newLink: a refusal comes back as an error", newLinkCalls == 2 && t.Contains("\"error\":\"Turn on access from other devices first.\""));
            newLinkAnswer = null;
            // the HTTPS switch: one of three kinds of request, a real boolean, and the answer carries the HTTPS state
            int secureCalls = 0; bool secureLast = false; string secureAnswer = null;
            g.HttpsSwitch = on => { secureCalls++; secureLast = on; return secureAnswer; };
            r = await postLan("{\"https\":true}");
            t = await r.Content.ReadAsStringAsync();
            Check("G https on: 200 and the handler got true", r.StatusCode == HttpStatusCode.OK && secureCalls == 1 && secureLast);
            r = await postLan("{\"https\":false}");
            Check("G https off: the handler got false", r.StatusCode == HttpStatusCode.OK && secureCalls == 2 && !secureLast);
            secureAnswer = "Turn on access from other devices first.";
            r = await postLan("{\"https\":true}");
            t = await r.Content.ReadAsStringAsync();
            Check("G https: a refusal comes back as an error", secureCalls == 3 && t.Contains("\"error\":\"Turn on access from other devices first.\""));
            secureAnswer = null;
            r = await postLan("{\"https\":\"yes\"}");
            Check("G https: not a boolean is 400", (int)r.StatusCode == 400 && secureCalls == 3);
            r = await postLan("{\"https\":true,\"enabled\":true}");
            Check("G https together with enabled is 400", (int)r.StatusCode == 400 && secureCalls == 3);
            r = await postLan("{\"https\":true,\"newLink\":true}");
            Check("G https together with newLink is 400", (int)r.StatusCode == 400 && secureCalls == 3);
            r = await postLan("{}");
            Check("G an empty request is 400", (int)r.StatusCode == 400);

            int calls = newLinkCalls;
            r = await postLan("{\"newLink\":false}");
            Check("G newLink false: 400", (int)r.StatusCode == 400);
            r = await postLan("{\"newLink\":\"true\"}");
            Check("G newLink as a string: 400", (int)r.StatusCode == 400);
            r = await postLan("{\"newLink\":1}");
            Check("G newLink as a number: 400", (int)r.StatusCode == 400);
            r = await postLan("{\"newLink\":true,\"enabled\":true}");
            Check("G newLink together with enabled: 400", (int)r.StatusCode == 400);
            r = await postLan("{}");
            Check("G neither field: 400", (int)r.StatusCode == 400);
            r = await postLan("{\"newLink\":null}");
            Check("G newLink null: 400", (int)r.StatusCode == 400);
            r = await Send("POST", B + "/lan", null, "http://evil.com", "application/json", "{\"newLink\":true}");
            Check("G newLink from another site: 403", (int)r.StatusCode == 403);
            r = await Send("POST", B + "/lan", null, null, "text/plain", "{\"newLink\":true}");
            Check("G newLink with the wrong content type: 415", (int)r.StatusCode == 415);
            Check("G none of the refused newLink requests reached the handler", newLinkCalls == calls);
            g.Stop();

            var noHandler = new MapServer(port + 1, null, null);
            noHandler.Start();
            r = await Send("POST", "http://127.0.0.1:" + (port + 1) + "/lan", null, null, "application/json", "{\"newLink\":true}");
            Check("G newLink with no handler: an error, no crash", r.StatusCode == HttpStatusCode.OK && (await r.Content.ReadAsStringAsync()).Contains("\"error\""));
            noHandler.Stop();

            // EnableLan reports a closing server
            var closing = new MapServer(port + 2, null, null);
            closing.Start();
            closing.Stop();
            Check("G EnableLan on a stopped server returns false", closing.EnableLan("0123456789abcdef0123456789abcdef", "127.0.0.2") == false);
            Thread.Sleep(300);
        }

        // H: the settings file and /mapinfo (1.2.3)
        {
            var hs = new MapServer(port + 3, null, null);
            hs.Start();
            string H = "http://127.0.0.1:" + (port + 3);
            string hOrigin = "http://127.0.0.1:" + (port + 3);

            // many saves at the same moment: the file stays one whole valid body, no temp file is left
            var bodies = new List<string>();
            for (int i = 0; i < 40; i++) bodies.Add("{\"save\":" + i + ",\"pad\":\"" + new string('x', 2000 + i * 37) + "\"}");
            var saves = new List<Task<HttpResponseMessage>>();
            foreach (var b in bodies) saves.Add(Send("POST", H + "/settings", origin: hOrigin, contentType: "application/json", body: b));
            await Task.WhenAll(saves);
            int ok204 = 0;
            foreach (var t in saves) if ((int)t.Result.StatusCode == 204) ok204++;
            Check("H 40 simultaneous saves all answered 204", ok204 == 40);
            string finalText = File.ReadAllText(settingsFile);
            Check("H after simultaneous saves the file is exactly one of the bodies", bodies.Contains(finalText));
            bool parses = true;
            try { Newtonsoft.Json.Linq.JObject.Parse(finalText); } catch { parses = false; }
            Check("H after simultaneous saves the file is valid JSON", parses);
            Check("H no temp file left behind", !File.Exists(settingsFile + ".tmp"));
            r = await Send("GET", H + "/settings");
            Check("H a read after the saves returns the same text", (await r.Content.ReadAsStringAsync()) == finalText);

            // a save with no file yet
            File.Delete(settingsFile);
            r = await Send("POST", H + "/settings", origin: hOrigin, contentType: "application/json", body: "{\"fresh\":true}");
            Check("H a save with no file yet works", (int)r.StatusCode == 204 && File.ReadAllText(settingsFile) == "{\"fresh\":true}");

            // /mapinfo is built by the serializer
            MapImageCapture.Png = new byte[] { 1, 2, 3 };
            MapImageCapture.MapSizeX = 12345.5f;
            MapImageCapture.MapSizeY = 6789f;
            MapImageCapture.OffsetX = 120000;
            MapImageCapture.OffsetY = 80000;
            MapImageCapture.MapName = "Odd \"quoted\" name \\ with a backslash";
            r = await Send("GET", H + "/mapinfo");
            string infoText = await r.Content.ReadAsStringAsync();
            Newtonsoft.Json.Linq.JObject info = null;
            try { info = Newtonsoft.Json.Linq.JObject.Parse(infoText); } catch { }
            Check("H mapinfo is valid JSON with a quote and a backslash in the name", info != null);
            Check("H mapinfo gives the name back unchanged", info != null && (string)info["mapName"] == MapImageCapture.MapName);
            Check("H mapinfo sizes are right", info != null && (float)info["mapSizeX"] == 12345.5f && (float)info["mapSizeY"] == 6789f);
            Check("H mapinfo carries the grid offsets of the map", info != null && (int)info["offsetX"] == 120000 && (int)info["offsetY"] == 80000);
            // the ocean depth map: 404 on a map without one, the bytes when there is one
            r = await Send("GET", H + "/mapwater.png");
            Check("H /mapwater.png is a 404 when the map has no ocean depth map", (int)r.StatusCode == 404);
            MapImageCapture.WaterPng = new byte[] { 9, 8, 7, 6 };
            r = await Send("GET", H + "/mapwater.png");
            byte[] waterBytes = await r.Content.ReadAsByteArrayAsync();
            Check("H /mapwater.png gives the depth map as a png", (int)r.StatusCode == 200 && r.Content.Headers.ContentType.MediaType == "image/png" && waterBytes.Length == 4 && waterBytes[0] == 9);
            MapImageCapture.WaterPng = null;
            MapImageCapture.OffsetX = 80000;
            MapImageCapture.Png = null;
            MapImageCapture.MapName = "";
            MapImageCapture.MapSizeX = 0;
            MapImageCapture.MapSizeY = 0;

            // /uniticon: the icon of a kind of unit, 404 while it is not taken yet, 410 for a kind without one
            r = await Send("GET", H + "/uniticon?type=Ready");
            Check("H /uniticon is a 404 while the icon is not taken yet", (int)r.StatusCode == 404);
            UnitIconCapture.Ready = new UnitIconCapture.Icon { Png = new byte[] { 5, 4, 3 }, Orient = true, Size = 1.5f, Sprite = "attackHelo1" };
            r = await Send("GET", H + "/uniticon?type=Ready");
            byte[] iconBytes = await r.Content.ReadAsByteArrayAsync();
            Check("H /uniticon gives the picture with its orient, size and sprite name", (int)r.StatusCode == 200 && iconBytes.Length == 3 && r.Content.Headers.ContentType.MediaType == "image/png"
                && System.Linq.Enumerable.First(r.Headers.GetValues("X-Orient")) == "1" && System.Linq.Enumerable.First(r.Headers.GetValues("X-Size")) == "1.5" && System.Linq.Enumerable.First(r.Headers.GetValues("X-Sprite")) == "attackHelo1");
            UnitIconCapture.Without = "Plain";
            r = await Send("GET", H + "/uniticon?type=Plain");
            Check("H /uniticon is a 410 for a kind without an icon", (int)r.StatusCode == 410);
            r = await Send("GET", H + "/uniticon");
            Check("H /uniticon without a type is a 400", (int)r.StatusCode == 400);
            UnitIconCapture.Ready = null;

            // /about gives the version and the label of a test release
            r = await Send("GET", H + "/about");
            Newtonsoft.Json.Linq.JObject about = null;
            try { about = Newtonsoft.Json.Linq.JObject.Parse(await r.Content.ReadAsStringAsync()); } catch { }
            Check("H /about answers with the version and the label", about != null && (string)about["version"] == Plugin.PluginVersion && (string)about["label"] == Plugin.BuildLabel);
            Check("H /about says which game version it runs on and which one it was checked with", about != null && (string)about["gameVersion"] == Plugin.GameVersion && (string)about["testedGameVersion"] == Plugin.TestedGameVersion);

            hs.Stop();
            if (File.Exists(settingsFile)) File.Delete(settingsFile);
        }

        // I: one stuck client must not hold up the others (1.2.3)
        {
            Plugin.Log.Lines.Clear();
            int ip = port + 4;
            var si = new MapServer(ip, null, null);
            si.Start();
            string I = "http://127.0.0.1:" + ip;

            // a healthy reader
            var evOk = await client.GetAsync(I + "/events", HttpCompletionOption.ResponseHeadersRead);
            var okStream = await evOk.Content.ReadAsStreamAsync();
            long okBytes = 0;
            var okReader = Task.Run(async () =>
            {
                var b = new byte[65536];
                int n;
                try { while ((n = await okStream.ReadAsync(b, 0, b.Length)) > 0) Interlocked.Add(ref okBytes, n); } catch { }
            });

            // a stuck one: asks for the stream with a tiny receive buffer and never reads
            var stuck = new System.Net.Sockets.TcpClient();
            stuck.ReceiveBufferSize = 1024;
            stuck.Connect("127.0.0.1", ip);
            byte[] ask = System.Text.Encoding.ASCII.GetBytes("GET /events HTTP/1.1\r\nHost: 127.0.0.1:" + ip + "\r\n\r\n");
            stuck.GetStream().Write(ask, 0, ask.Length);
            Thread.Sleep(400);

            string big = new string('x', 300000);
            var sender = Task.Run(() =>
            {
                for (int i = 0; i < 40; i++)
                {
                    si.Broadcast("{\"i\":" + i + ",\"pad\":\"" + big + "\"}");
                    Thread.Sleep(50);
                }
            });
            bool sentInTime = sender.Wait(10000);
            Check("I 40 big snapshots go out while one client is stuck (Broadcast never waits for it)", sentInTime);
            Thread.Sleep(600);
            Check("I the healthy client got (nearly) all of them", Interlocked.Read(ref okBytes) > 35L * 300000);

            // the stuck client is cut off after a while, the healthy one carries on
            for (int i = 0; i < 70 && !Plugin.Log.Lines.Exists(l => l.Contains("dropped an event stream")); i++)
            {
                si.Broadcast("{\"late\":" + i + "}");
                Thread.Sleep(100);
            }
            Check("I the stuck client is dropped and logged", Plugin.Log.Lines.Exists(l => l.Contains("dropped an event stream")));
            long before = Interlocked.Read(ref okBytes);
            si.Broadcast("{\"after\":true}");
            Thread.Sleep(300);
            Check("I the healthy client still gets data after the drop", Interlocked.Read(ref okBytes) > before);
            r = await Send("GET", I + "/");
            Check("I the page still answers", r.StatusCode == HttpStatusCode.OK);

            stuck.Close();
            evOk.Dispose();
            si.Stop();
            Thread.Sleep(300);
        }

        Console.WriteLine("\n" + pass + " passed, " + fail + " failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
