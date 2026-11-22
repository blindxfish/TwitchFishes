using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class OverlayControlsServer : IDisposable
{
    private const int MaximumBodyBytes = 4096;
    private const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; " +
        "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private sealed class Request
    {
        public readonly string Body;
        public readonly TaskCompletionSource<Reply> Completed = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);

        public Request(string body)
        {
            Body = body;
        }
    }

    internal readonly struct Reply
    {
        public readonly int Status;
        public readonly string Json;

        public Reply(int status, string json)
        {
            Status = status;
            Json = json;
        }
    }

    private readonly HttpListener listener = new HttpListener();
    private readonly ConcurrentQueue<Request> requests = new ConcurrentQueue<Request>();
    private readonly SemaphoreSlim slots = new SemaphoreSlim(12);
    private readonly string page;
    private readonly string css;
    private readonly string javascript;
    private readonly string accessKey;
    private readonly Uri address;

    public string DashboardUrl => $"{address.AbsoluteUri}#{accessKey}";

    public OverlayControlsServer(string page, string css, string javascript)
    {
        this.page = page;
        this.css = css;
        this.javascript = javascript;

        var secret = new byte[24];
        using (var random = RandomNumberGenerator.Create())
            random.GetBytes(secret);
        accessKey = Convert.ToBase64String(secret).Replace('+', '-').Replace('/', '_');

        var ports = new Random();
        try
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                address = new Uri($"http://127.0.0.1:{ports.Next(49152, 65536)}/");
                listener.Prefixes.Clear();
                listener.Prefixes.Add(address.AbsoluteUri);
                try
                {
                    listener.Start();
                    break;
                }
                catch (HttpListenerException) when (attempt < 9) { }
            }

            _ = AcceptRequests();
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    public void Update(Func<string, Reply> handle)
    {
        for (int i = 0; i < 12 && requests.TryDequeue(out Request request); i++)
        {
            if (request.Completed.Task.IsCompleted)
                continue;

            request.Completed.TrySetResult(handle(request.Body));
        }
    }

    public void Dispose()
    {
        listener.Close();
        while (requests.TryDequeue(out Request request))
            request.Completed.TrySetCanceled();
    }

    private async Task AcceptRequests()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }
            catch (HttpListenerException) { return; }

            if (slots.Wait(0))
                _ = Serve(context);
            else
            {
                context.Response.StatusCode = 503;
                context.Response.Close();
            }
        }
    }

    private async Task Serve(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["X-Frame-Options"] = "DENY";
            response.Headers["Cache-Control"] = "no-store";
            response.KeepAlive = false;

            if (!IsLocal(request))
            {
                response.StatusCode = 403;
                return;
            }

            string path = request.Url.AbsolutePath;
            if (request.HttpMethod == "GET")
            {
                switch (path)
                {
                    case "/":
                        await Write(response, 200, "text/html", page).ConfigureAwait(false);
                        return;
                    case "/dashboard.css":
                        await Write(response, 200, "text/css", css).ConfigureAwait(false);
                        return;
                    case "/dashboard.js":
                        await Write(response, 200, "text/javascript", javascript).ConfigureAwait(false);
                        return;
                    case "/api/state":
                        if (IsAuthorized(request))
                            await Dispatch(response, null).ConfigureAwait(false);
                        else
                            response.StatusCode = 401;
                        return;
                }
            }

            if (request.HttpMethod == "POST" && path == "/api/action")
            {
                if (!IsAuthorized(request))
                {
                    response.StatusCode = 401;
                    return;
                }

                if (request.ContentLength64 <= 0 || request.ContentLength64 > MaximumBodyBytes ||
                    !string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 400;
                    return;
                }

                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                Task<string> read = reader.ReadToEndAsync();
                if (await Task.WhenAny(read, Task.Delay(2500)).ConfigureAwait(false) != read)
                {
                    response.StatusCode = 408;
                    return;
                }

                await Dispatch(response, await read.ConfigureAwait(false)).ConfigureAwait(false);
                return;
            }

            response.StatusCode = 404;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or HttpListenerException or OperationCanceledException)
        {
        }
        finally
        {
            try { response.Close(); }
            finally { slots.Release(); }
        }
    }

    private bool IsLocal(HttpListenerRequest request)
    {
        if (request.RemoteEndPoint == null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
            !string.Equals(request.Headers["Host"], address.Authority, StringComparison.OrdinalIgnoreCase))
            return false;

        string origin = request.Headers["Origin"];
        return string.IsNullOrEmpty(origin) || string.Equals(origin, address.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
    }

    private bool IsAuthorized(HttpListenerRequest request) =>
        string.Equals(request.Headers["Authorization"], $"Bearer {accessKey}", StringComparison.Ordinal);

    private async Task Dispatch(HttpListenerResponse response, string body)
    {
        var work = new Request(body);
        requests.Enqueue(work);
        Task<Reply> reply = work.Completed.Task;
        if (await Task.WhenAny(reply, Task.Delay(4000)).ConfigureAwait(false) != reply)
        {
            work.Completed.TrySetCanceled();
            response.StatusCode = 503;
            return;
        }

        Reply result = await reply.ConfigureAwait(false);
        await Write(response, result.Status, "application/json", result.Json).ConfigureAwait(false);
    }

    private static async Task Write(HttpListenerResponse response, int status, string contentType, string data)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(data);
        response.StatusCode = status;
        response.ContentType = $"{contentType}; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }
}