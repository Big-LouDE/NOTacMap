using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
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
    }
    internal static class Plugin
    {
        public static FakeLog Log = new FakeLog();
        public static string ThemeJson = null;
    }
    internal static class MapImageCapture
    {
        public static byte[] Png = null;
        public static float MapSizeX = 0, MapSizeY = 0;
        public static string MapName = "";
        public static int OffsetX = 80000, OffsetY = 80000;
    }
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
            MapImageCapture.OffsetX = 80000;
            MapImageCapture.Png = null;
            MapImageCapture.MapName = "";
            MapImageCapture.MapSizeX = 0;
            MapImageCapture.MapSizeY = 0;

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
