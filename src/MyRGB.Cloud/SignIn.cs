using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace MyRGB.Cloud;

/// <summary>PKCE (RFC 7636): proves the app that started sign-in is the one finishing it, without a client secret.</summary>
internal static class Pkce
{
    public static (string Verifier, string Challenge) Create()
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Browser sign-in for a desktop app (RFC 8252): open the provider's page in the user's own
/// browser, and catch the redirect back on a localhost port. MyRGB never sees a password.
/// </summary>
internal static class BrowserSignIn
{
    /// <summary>Redirect ports; each must be on the Supabase project's redirect allow-list.</summary>
    public static readonly int[] Ports = [6744, 6745, 6746];

    private const string DonePage = """
        <!doctype html><html><head><meta charset="utf-8"><title>MyRGB</title>
        <style>body{font-family:Segoe UI,sans-serif;background:#0b0b12;color:#e8e8f0;display:grid;place-items:center;height:100vh;margin:0}
        div{text-align:center}h1{font-size:22px}p{color:#8a8fa8}</style></head>
        <body><div><h1>{0}</h1><p>{1}</p></div></body></html>
        """;

    /// <summary>Returns the authorization code, or throws <see cref="CloudException"/> if sign-in was refused.</summary>
    public static async Task<string> GetCodeAsync(Func<string, string> authorizeUrl, CancellationToken ct)
    {
        using var listener = Listen(out int port);
        string redirect = $"http://localhost:{port}/callback";
        Browser.Open(authorizeUrl(redirect));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var _ = timeout.Token.Register(listener.Stop);

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                throw ct.IsCancellationRequested ? new OperationCanceledException(ct) : new CloudException("Sign-in timed out. Please try again.");
            }

            if (ctx.Request.Url?.AbsolutePath != "/callback")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue; // e.g. the browser asking for /favicon.ico
            }

            var query = HttpUtility.ParseQueryString(ctx.Request.Url.Query);
            string? code = query["code"];
            string? error = query["error_description"] ?? query["error"];
            await Respond(ctx, code is not null
                ? ("You're signed in to MyRGB", "You can close this tab and go back to the app.")
                : ("Sign-in didn't finish", error ?? "Please go back to MyRGB and try again."));

            if (code is not null) return code;
            throw new CloudException(error ?? "Sign-in was cancelled.");
        }
    }

    private static HttpListener Listen(out int port)
    {
        foreach (var p in Ports)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{p}/");
            try
            {
                listener.Start();
                port = p;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close(); // port taken; try the next
            }
        }
        throw new CloudException("MyRGB couldn't open a local port to finish sign-in. Close other copies of MyRGB and try again.");
    }

    private static async Task Respond(HttpListenerContext ctx, (string Title, string Text) message)
    {
        var html = DonePage.Replace("{0}", WebUtility.HtmlEncode(message.Title)).Replace("{1}", WebUtility.HtmlEncode(message.Text));
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}
