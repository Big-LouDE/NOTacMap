// Tests for RequestGuard: which requests are let through, and how the LAN
// address is chosen. Runs on .NET Framework, the same runtime family as the
// plugin (some address handling differs from modern .NET).
//   dotnet run --project tests/GuardTests -c Release
using System;
using System.Collections.Generic;
using System.Net;
using NOTacMap;

internal static class Program
{
    static int pass, fail;
    const string TOKEN = "0123456789abcdef0123456789abcdef";
    static readonly IPAddress Pc = IPAddress.Parse("127.0.0.1");
    static readonly IPAddress Phone = IPAddress.Parse("192.168.1.77");

    static void Check(string name, bool ok)
    {
        if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + name); }
    }

    static GuardDecision Ask(RequestGuard g, string host, string origin, IPAddress from, string method = "GET",
        string path = "/", string query = "", string cookie = null)
        => g.Evaluate(host, origin, from, method, path, query, cookie);

    static void Main()
    {
        var lan = new RequestGuard(8123, "192.168.1.10", TOKEN);
        var off = new RequestGuard(8123, null, null);
        string LH = "192.168.1.10:8123";

        // ---- this PC, unchanged behaviour
        Check("pc page", Ask(lan, "localhost:8123", null, Pc).Status == 0);
        Check("pc page by ip", Ask(lan, "127.0.0.1:8123", null, Pc).Status == 0);
        Check("pc ipv6 loopback", Ask(lan, "localhost:8123", null, IPAddress.IPv6Loopback).Status == 0);
        Check("pc ipv4-mapped loopback", Ask(lan, "localhost:8123", null, IPAddress.Parse("::ffff:127.0.0.1")).Status == 0);
        Check("pc settings save same origin", Ask(lan, "localhost:8123", "http://localhost:8123", Pc, "POST", "/settings").Status == 0);
        Check("pc /lan", Ask(lan, "localhost:8123", null, Pc, "GET", "/lan").Status == 0);
        Check("pc via lan host name (allowed host)", Ask(lan, LH, null, Pc).Status == 0);
        Check("spoofed host", Ask(lan, "evil.com", null, Pc).Status == 403);
        Check("rebind host", Ask(lan, "127.0.0.1.evil.com:8123", null, Pc).Status == 403);
        Check("null host", Ask(lan, null, null, Pc).Status == 403);
        Check("foreign origin", Ask(lan, "localhost:8123", "http://evil.com", Pc, "POST", "/settings").Status == 403);
        Check("origin null", Ask(lan, "localhost:8123", "null", Pc).Status == 403);
        Check("https origin", Ask(lan, "localhost:8123", "https://localhost:8123", Pc).Status == 403);
        Check("DELETE", Ask(lan, "localhost:8123", null, Pc, "DELETE").Status == 405);
        Check("PUT settings", Ask(lan, "localhost:8123", null, Pc, "PUT", "/settings").Status == 405);
        Check("POST elsewhere", Ask(lan, "localhost:8123", null, Pc, "POST", "/events").Status == 405);

        // ---- LAN switched off: nothing from other devices gets in
        Check("off: phone with right host", Ask(off, LH, null, Phone).Status == 403);
        Check("off: phone with localhost host", Ask(off, "localhost:8123", null, Phone, query: "t=" + TOKEN).Status == 403);
        Check("off: pc still fine", Ask(off, "localhost:8123", null, Pc).Status == 0);
        Check("off: unknown remote address is let through", Ask(off, "localhost:8123", null, null).Status == 0);
        Check("off: unknown remote still needs the right host", Ask(off, "evil.com", null, null).Status == 403);
        Check("on: unknown remote address is refused", Ask(lan, LH, null, null, cookie: "notacmap_t=" + TOKEN).Status == 403);

        // ---- phone, no token
        Check("phone no token", Ask(lan, LH, null, Phone).Status == 401);
        Check("phone 401 has a message", Ask(lan, LH, null, Phone).Message != null);
        Check("phone wrong token", Ask(lan, LH, null, Phone, query: "t=nope").Status == 401);
        Check("phone empty token", Ask(lan, LH, null, Phone, query: "t=").Status == 401);
        Check("phone token prefix only", Ask(lan, LH, null, Phone, query: "t=" + TOKEN.Substring(0, 31)).Status == 401);
        Check("phone token plus extra", Ask(lan, LH, null, Phone, query: "t=" + TOKEN + "x").Status == 401);
        Check("phone wrong cookie", Ask(lan, LH, null, Phone, cookie: "notacmap_t=nope").Status == 401);
        Check("phone cookie wrong name", Ask(lan, LH, null, Phone, cookie: "other=" + TOKEN).Status == 401);
        Check("phone events no token", Ask(lan, LH, null, Phone, path: "/events").Status == 401);

        // ---- phone, link with the token: cookie and clean redirect
        var first = Ask(lan, LH, null, Phone, query: "?t=" + TOKEN);
        Check("token link redirects", first.Status == 302 && first.RedirectTo == "/");
        Check("token link sets cookie", first.SetCookie != null && first.SetCookie.StartsWith("notacmap_t=" + TOKEN)
            && first.SetCookie.Contains("HttpOnly") && first.SetCookie.Contains("SameSite=Strict"));
        Check("token link keeps other params", Ask(lan, LH, null, Phone, query: "a=1&t=" + TOKEN + "&b=2").RedirectTo == "/?a=1&b=2");
        Check("token via url-encoded value", Ask(lan, LH, null, Phone, query: "t=%30123456789abcdef0123456789abcdef").Status == 302);
        Check("token link on a path", Ask(lan, LH, null, Phone, path: "/index.html", query: "t=" + TOKEN).RedirectTo == "/index.html");
        Check("no redirect to another site (//)", Ask(lan, LH, null, Phone, path: "//evil.com/x", query: "t=" + TOKEN).RedirectTo == "/");
        Check("no redirect with backslash", Ask(lan, LH, null, Phone, path: "/\\evil.com", query: "t=" + TOKEN).RedirectTo == "/");
        Check("token in url not accepted for events", Ask(lan, LH, null, Phone, path: "/events", query: "t=" + TOKEN).Status == 401);
        Check("token link on POST not accepted", Ask(lan, LH, "http://" + LH, Phone, "POST", "/settings", "t=" + TOKEN).Status == 403);

        // ---- phone with the cookie
        string ck = "notacmap_t=" + TOKEN;
        Check("cookie page", Ask(lan, LH, null, Phone, cookie: ck).Status == 0);
        Check("cookie among others", Ask(lan, LH, null, Phone, cookie: "a=b; " + ck + "; c=d").Status == 0);
        Check("cookie events", Ask(lan, LH, null, Phone, path: "/events", cookie: ck).Status == 0);
        Check("cookie theme", Ask(lan, LH, null, Phone, path: "/theme", cookie: ck).Status == 0);
        Check("cookie map image", Ask(lan, LH, null, Phone, path: "/mapimage.png", cookie: ck).Status == 0);
        Check("cookie settings read", Ask(lan, LH, null, Phone, path: "/settings", cookie: ck).Status == 0);
        Check("cookie, same-origin origin header", Ask(lan, LH, "http://" + LH, Phone, cookie: ck).Status == 0);

        // ---- phone must never write settings or read the phone link
        Check("phone cannot save settings", Ask(lan, LH, "http://" + LH, Phone, "POST", "/settings", cookie: ck).Status == 403);
        Check("phone cannot read /lan", Ask(lan, LH, null, Phone, "GET", "/lan", cookie: ck).Status == 403);
        Check("phone: foreign origin", Ask(lan, LH, "http://evil.com", Phone, cookie: ck).Status == 403);
        Check("phone: wrong host with cookie", Ask(lan, "evil.com", null, Phone, cookie: ck).Status == 403);

        // ---- addresses that are not on a home network
        foreach (var bad in new[] { "8.8.8.8", "100.64.0.5", "172.32.0.1", "172.15.0.1", "192.169.1.1", "11.0.0.1", "1.1.1.1" })
            Check("public/other address refused: " + bad, Ask(lan, LH, null, IPAddress.Parse(bad), cookie: ck).Status == 403);
        foreach (var good in new[] { "10.0.0.5", "10.255.255.254", "172.16.0.1", "172.31.255.1", "192.168.0.1", "169.254.3.4" })
            Check("private address accepted: " + good, Ask(lan, LH, null, IPAddress.Parse(good), cookie: ck).Status == 0);
        Check("ipv6 remote refused", Ask(lan, LH, null, IPAddress.Parse("fe80::1"), cookie: ck).Status == 403);
        Check("ipv4-mapped private accepted", Ask(lan, LH, null, IPAddress.Parse("::ffff:192.168.1.5"), cookie: ck).Status == 0);
        Check("null remote refused", Ask(lan, LH, null, null, cookie: ck).Status == 403);

        // ---- address helpers
        Check("IsPrivate null", !LanAddress.IsPrivate(null));
        var cands = new List<KeyValuePair<IPAddress, bool>>
        {
            new KeyValuePair<IPAddress, bool>(IPAddress.Parse("169.254.9.9"), false),
            new KeyValuePair<IPAddress, bool>(IPAddress.Parse("8.8.8.8"), true),
            new KeyValuePair<IPAddress, bool>(IPAddress.Parse("10.1.1.5"), false),
            new KeyValuePair<IPAddress, bool>(IPAddress.Parse("192.168.1.10"), true),
        };
        Check("pick prefers private with gateway", LanAddress.Pick(cands).ToString() == "192.168.1.10");
        cands.RemoveAt(3);
        Check("pick falls back to first private", LanAddress.Pick(cands).ToString() == "169.254.9.9");
        Check("pick none", LanAddress.Pick(new List<KeyValuePair<IPAddress, bool>>()) == null);
        string why;
        Check("configured public refused", LanAddress.Resolve("8.8.8.8", out why) == null && why != null);
        Check("configured junk refused", LanAddress.Resolve("not an address", out why) == null && why != null);
        Check("configured private used", LanAddress.Resolve(" 192.168.5.5 ", out why) == "192.168.5.5" && why == null);
        string auto = LanAddress.Resolve("", out why);
        Console.WriteLine("(auto-pick on this machine: " + (auto ?? ("none, " + why)) + ")");

        // ---- parsing helpers
        string rem;
        Check("cookie parse", RequestGuard.ReadCookie("x=1;  notacmap_t=abc ; y=2", "notacmap_t") == "abc");
        Check("cookie parse missing", RequestGuard.ReadCookie("x=1", "notacmap_t") == null);
        Check("cookie parse empty header", RequestGuard.ReadCookie("", "notacmap_t") == null);
        Check("query parse", RequestGuard.ReadQueryValue("?a=1&t=zz&b=2", "t", out rem) == "zz" && rem == "a=1&b=2");
        Check("query parse none", RequestGuard.ReadQueryValue("a=1", "t", out rem) == null && rem == "a=1");
        Check("query parse bad escape", RequestGuard.ReadQueryValue("t=%zz", "t", out rem) != null);
        Check("query parse first t wins", RequestGuard.ReadQueryValue("t=1&t=2", "t", out rem) == "1" && rem == "t=2");

        Console.WriteLine($"\n{pass} passed, {fail} failed");
        Environment.Exit(fail == 0 ? 0 : 1);
    }
}
