using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[Serializable]
internal sealed class TwitchRewardDefinition
{
    public string id = "";
    public string title = "";
    public int cost = 0;
    public bool is_enabled = false;
    public bool is_paused = false;
    public bool is_in_stock = true;
}

[Serializable]
internal sealed class TwitchRedemption
{
    public string id = "";
    public string broadcaster_user_id = "";
    public string user_login = "";
    public string user_name = "";
    public TwitchRewardDefinition reward = null;
}

internal sealed class TwitchRewardConnection : IDisposable
{
    private const string RedemptionType = "channel.channel_points_custom_reward_redemption.add";
    private const string Helix = "https://api.twitch.tv/helix";
    private static readonly Uri Endpoint = new Uri("wss://eventsub.wss.twitch.tv/ws");
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public enum EventType { Catalog, Listening, Retrying, Error, RefreshFailed, Redemption }

    public sealed class RewardEvent
    {
        public EventType Type;
        public string Detail;
        public string BroadcasterId;
        public string Channel;
        public TwitchRewardDefinition[] Catalog;
        public TwitchRedemption Redemption;
    }

    [Serializable]
    private sealed class Identity
    {
        public string client_id = "";
        public string login = "";
        public string user_id = "";
        public string[] scopes = null;
        public int expires_in = 0;
    }

    [Serializable]
    private sealed class RewardList
    {
        public TwitchRewardDefinition[] data = null;
    }

    [Serializable]
    private sealed class Envelope
    {
        public Metadata metadata = null;
        public Payload payload = null;
    }

    [Serializable]
    private sealed class Metadata
    {
        public string message_type = "";
        public string subscription_type = "";
    }

    [Serializable]
    private sealed class Payload
    {
        public SessionInfo session = null;
        public TwitchRedemption @event = null;
    }

    [Serializable]
    private sealed class SessionInfo
    {
        public string id = "";
        public int keepalive_timeout_seconds = 0;
        public string reconnect_url = "";
    }

    [Serializable]
    private sealed class Subscription
    {
        public string type = RedemptionType;
        public string version = "1";
        public Condition condition;
        public Transport transport;
    }

    [Serializable]
    private sealed class Condition
    {
        public string broadcaster_user_id;
    }

    [Serializable]
    private sealed class Transport
    {
        public string method = "websocket";
        public string session_id;
    }

    private sealed class AuthorizationError : Exception
    {
        public AuthorizationError(string message) : base(message) { }
    }

    private sealed class Session : IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly byte[] buffer = new byte[8192];
        public SessionInfo Info { get; private set; }

        public async Task OpenAsync(Uri uri, CancellationToken cancellation)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
            Envelope welcome = await ReadAsync(timeout.Token).ConfigureAwait(false);
            Info = welcome.metadata?.message_type == "session_welcome" ? welcome.payload?.session : null;
            if (string.IsNullOrEmpty(Info?.id))
                throw new InvalidDataException("EventSub did not provide a session ID.");
        }

        public async Task<Envelope> ReadAsync(CancellationToken cancellation)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            int seconds = Info == null ? 15 : Math.Max(Info.keepalive_timeout_seconds, 10) + 5;
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var message = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                if (frame.MessageType != WebSocketMessageType.Text)
                    throw new IOException("The EventSub WebSocket closed or sent a non-text frame.");
                message.Write(buffer, 0, frame.Count);
                if (message.Length > 1024 * 1024)
                    throw new InvalidDataException("An EventSub message exceeded the receive limit.");
            }
            while (!frame.EndOfMessage);

            string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            return JsonUtility.FromJson<Envelope>(json) ?? throw new InvalidDataException("Empty EventSub message.");
        }

        public void Dispose() => socket.Dispose();
    }

    private readonly ConcurrentQueue<RewardEvent> events = new ConcurrentQueue<RewardEvent>();
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly string token;
    private readonly string expectedChannel;
    private readonly Task connectionTask;
    private Task refreshTask = Task.CompletedTask;
    private volatile Identity identity;

    public TwitchRewardConnection(string token, string expectedChannel)
    {
        this.token = token;
        this.expectedChannel = expectedChannel;
        connectionTask = RunAsync(stop.Token);
    }

    public bool TryRead(out RewardEvent message) => events.TryDequeue(out message);

    public bool Refresh()
    {
        Identity current = identity;
        if (current == null || !refreshTask.IsCompleted)
            return false;

        refreshTask = RefreshAsync(current, stop.Token);
        return true;
    }

    public void Dispose()
    {
        stop.Cancel();
        _ = DisposeWhenFinished();
    }

    private async Task DisposeWhenFinished()
    {
        try { await Task.WhenAll(connectionTask, refreshTask).ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }

    private async Task RunAsync(CancellationToken cancellation)
    {
        int retryDelay = 2;
        while (!cancellation.IsCancellationRequested)
        {
            Session session = null;
            try
            {
                Identity current = await ValidateAsync(cancellation).ConfigureAwait(false);
                await LoadCatalogAsync(current, cancellation).ConfigureAwait(false);
                identity = current;
                session = await OpenSessionAsync(Endpoint, cancellation).ConfigureAwait(false);
                await SubscribeAsync(current, session.Info.id, cancellation).ConfigureAwait(false);
                retryDelay = 2;
                events.Enqueue(new RewardEvent { Type = EventType.Listening, Channel = current.login });
                DateTime validateAt = NextValidation(current);

                while (!cancellation.IsCancellationRequested)
                {
                    Envelope message = await session.ReadAsync(cancellation).ConfigureAwait(false);
                    if (message.metadata?.message_type == "session_reconnect")
                    {
                        string target = message.payload?.session?.reconnect_url;
                        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri reconnect) || reconnect.Scheme != "wss" ||
                            !string.Equals(reconnect.Host, Endpoint.Host, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Twitch returned an invalid EventSub reconnect address.");

                        session = await HandoffAsync(session, reconnect, current.user_id, cancellation).ConfigureAwait(false);
                    }
                    else
                    {
                        ReadNotification(message, current.user_id);
                    }

                    if (DateTime.UtcNow >= validateAt)
                    {
                        current = await ValidateAsync(cancellation).ConfigureAwait(false);
                        validateAt = NextValidation(current);
                    }
                }
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (AuthorizationError error)
            {
                events.Enqueue(new RewardEvent { Type = EventType.Error, Detail = error.Message });
                return;
            }
            catch (PlatformNotSupportedException)
            {
                events.Enqueue(new RewardEvent { Type = EventType.Error, Detail = "Twitch rewards are unavailable on this platform." });
                return;
            }
            catch (Exception)
            {
                events.Enqueue(new RewardEvent { Type = EventType.Retrying, Detail = "Reward connection interrupted. Retrying automatically…" });
            }
            finally
            {
                session?.Dispose();
            }

            try { await Task.Delay(TimeSpan.FromSeconds(retryDelay), cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            retryDelay = Math.Min(retryDelay * 2, 30);
        }
    }

    private static DateTime NextValidation(Identity current) =>
        DateTime.UtcNow.AddSeconds(Math.Max(5, Math.Min(3500, current.expires_in - 5)));

    private async Task<Identity> ValidateAsync(CancellationToken cancellation)
    {
        string json = await RequestAsync(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate", null, null,
            "Twitch could not validate the broadcaster token.", cancellation).ConfigureAwait(false);
        var current = JsonUtility.FromJson<Identity>(json);
        if (string.IsNullOrEmpty(current?.client_id) || string.IsNullOrEmpty(current.user_id) ||
            string.IsNullOrEmpty(current.login))
            throw new AuthorizationError("Use a Twitch user access token from the channel owner.");

        if (!string.IsNullOrEmpty(expectedChannel) && !string.Equals(current.login, expectedChannel, StringComparison.OrdinalIgnoreCase))
            throw new AuthorizationError($"Use a token authorized by #{expectedChannel}, the channel set in Twitch settings.");

        if (current.scopes == null ||
            (Array.IndexOf(current.scopes, "channel:read:redemptions") < 0 && Array.IndexOf(current.scopes, "channel:manage:redemptions") < 0))
            throw new AuthorizationError("The broadcaster token needs the channel:read:redemptions scope.");

        return current;
    }

    private async Task LoadCatalogAsync(Identity current, CancellationToken cancellation)
    {
        string url = $"{Helix}/channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(current.user_id)}";
        string json = await RequestAsync(HttpMethod.Get, url, current.client_id, null,
            "Twitch could not list custom rewards. Check Channel Points access in the Creator Dashboard.", cancellation)
            .ConfigureAwait(false);
        TwitchRewardDefinition[] catalog = JsonUtility.FromJson<RewardList>(json)?.data ?? Array.Empty<TwitchRewardDefinition>();
        events.Enqueue(new RewardEvent
        {
            Type = EventType.Catalog,
            Catalog = catalog,
            BroadcasterId = current.user_id,
            Channel = current.login
        });
    }

    private async Task RefreshAsync(Identity current, CancellationToken cancellation)
    {
        try { await LoadCatalogAsync(current, cancellation).ConfigureAwait(false); }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        catch (AuthorizationError error)
        {
            events.Enqueue(new RewardEvent { Type = EventType.RefreshFailed, Detail = error.Message });
        }
        catch (Exception)
        {
            events.Enqueue(new RewardEvent { Type = EventType.RefreshFailed, Detail = "Could not refresh rewards. Try again." });
        }
    }

    private Task<string> SubscribeAsync(Identity current, string sessionId, CancellationToken cancellation)
    {
        var body = new Subscription
        {
            condition = new Condition { broadcaster_user_id = current.user_id },
            transport = new Transport { session_id = sessionId }
        };
        return RequestAsync(HttpMethod.Post, $"{Helix}/eventsub/subscriptions", current.client_id, JsonUtility.ToJson(body),
            "Twitch refused the redemption subscription. Check the broadcaster token and retry.", cancellation);
    }

    private async Task<string> RequestAsync(HttpMethod method, string url, string clientId, string json,
        string errorMessage, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrEmpty(clientId))
            request.Headers.Add("Client-Id", clientId);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthorizationError("Twitch rejected the broadcaster token. Enter a valid user access token.");
        int status = (int)response.StatusCode;
        if (status >= 400 && status < 500 && status != 409 && status != 429)
            throw new AuthorizationError(errorMessage);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private static async Task<Session> OpenSessionAsync(Uri address, CancellationToken cancellation)
    {
        var session = new Session();
        try
        {
            await session.OpenAsync(address, cancellation).ConfigureAwait(false);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private async Task<Session> HandoffAsync(Session current, Uri address, string broadcasterId, CancellationToken cancellation)
    {
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<Session> replacement = OpenSessionAsync(address, transfer.Token);
        try
        {
            while (!replacement.IsCompleted)
            {
                Task<Envelope> oldMessage = current.ReadAsync(transfer.Token);
                await Task.WhenAny(replacement, oldMessage).ConfigureAwait(false);
                if (oldMessage.IsCompletedSuccessfully || !replacement.IsCompleted)
                    ReadNotification(await oldMessage.ConfigureAwait(false), broadcasterId);
                else
                {
                    transfer.Cancel();
                    try { ReadNotification(await oldMessage.ConfigureAwait(false), broadcasterId); }
                    catch (Exception) when (transfer.IsCancellationRequested) { }
                }
            }

            Session verified = await replacement.ConfigureAwait(false);
            current.Dispose();
            return verified;
        }
        catch
        {
            transfer.Cancel();
            try { (await replacement.ConfigureAwait(false)).Dispose(); }
            catch (Exception) { }
            throw;
        }
    }

    private void ReadNotification(Envelope message, string broadcasterId)
    {
        Metadata metadata = message.metadata;
        if (metadata?.subscription_type != RedemptionType)
            return;
        if (metadata.message_type == "revocation")
            throw new AuthorizationError("Twitch revoked reward access. Check the channel and reconnect with a valid token.");
        if (metadata.message_type != "notification")
            return;

        TwitchRedemption redemption = message.payload?.@event;
        if (!string.IsNullOrEmpty(redemption?.id) && redemption.broadcaster_user_id == broadcasterId &&
            !string.IsNullOrEmpty(redemption.reward?.id) && !string.IsNullOrEmpty(redemption.user_login))
            events.Enqueue(new RewardEvent { Type = EventType.Redemption, Redemption = redemption });
    }
}