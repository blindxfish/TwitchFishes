using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

internal sealed class TwitchIrcConnection : IDisposable
{
    internal enum EventType { Joined, Chat, Retry, Error }

    internal readonly struct ChatEvent
    {
        public readonly EventType Type;
        public readonly string Text;
        public readonly string Login;
        public readonly string DisplayName;
        public readonly string Id;

        public ChatEvent(EventType type, string text, string login = "", string displayName = "", string id = "")
        {
            Type = type;
            Text = text;
            Login = login;
            DisplayName = displayName;
            Id = id;
        }
    }

    private static readonly Uri Endpoint = new Uri("wss://irc-ws.chat.twitch.tv:443");
    private static readonly Regex LinePattern = new Regex(
        @"\A(?:@(?<tags>[^ ]+) )?(?::(?<prefix>[^ ]+) )?(?<command>[^ ]+)(?: (?<arguments>.*))?\z",
        RegexOptions.CultureInvariant);

    private readonly ConcurrentQueue<ChatEvent> events = new ConcurrentQueue<ChatEvent>();
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly Task connectionTask;

    public TwitchIrcConnection(TwitchSettings settings)
    {
        connectionTask = RunAsync(settings, stop.Token);
    }

    public bool TryRead(out ChatEvent chatEvent) => events.TryDequeue(out chatEvent);

    public void Dispose()
    {
        stop.Cancel();
        _ = DisposeWhenFinished();
    }

    private async Task DisposeWhenFinished()
    {
        try { await connectionTask.ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }

    private async Task RunAsync(TwitchSettings settings, CancellationToken cancellation)
    {
        bool joined = false;
        bool authenticated = false;
        string channel = $"#{settings.channel}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        CancellationToken receiving = timeout.Token;
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(Endpoint, receiving).ConfigureAwait(false);
            await SendAsync(socket, "CAP REQ :twitch.tv/tags twitch.tv/commands", receiving).ConfigureAwait(false);
            await SendAsync(socket, $"PASS oauth:{settings.token}", receiving).ConfigureAwait(false);
            await SendAsync(socket, $"NICK {settings.login}", receiving).ConfigureAwait(false);

            var buffer = new byte[4096];
            using var message = new MemoryStream();
            while (!receiving.IsCancellationRequested)
            {
                WebSocketReceiveResult frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), receiving).ConfigureAwait(false);
                if (frame.MessageType == WebSocketMessageType.Close)
                    break;

                message.Write(buffer, 0, frame.Count);
                if (message.Length > 1024 * 1024)
                    throw new InvalidDataException("The chat message exceeded the receive limit.");
                if (!frame.EndOfMessage)
                    continue;

                string payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                foreach (string line in payload.Split('\n'))
                {
                    Match match = LinePattern.Match(line.TrimEnd('\r'));
                    if (!match.Success)
                        continue;

                    string command = match.Groups["command"].Value;
                    string arguments = match.Groups["arguments"].Value;
                    if (command == "PING")
                    {
                        await SendAsync(socket, $"PONG {arguments}", receiving).ConfigureAwait(false);
                        continue;
                    }

                    if (command == "001")
                    {
                        authenticated = true;
                        await SendAsync(socket, $"JOIN {channel}", receiving).ConfigureAwait(false);
                    }
                    else if (!joined && command == "ROOMSTATE" && arguments == channel)
                    {
                        joined = true;
                        timeout.CancelAfter(Timeout.Infinite);
                        events.Enqueue(new ChatEvent(EventType.Joined, channel));
                    }
                    else if (command == "PRIVMSG" && TryParseChat(match, channel, out ChatEvent chat))
                    {
                        events.Enqueue(chat);
                    }
                    else if (command == "RECONNECT")
                    {
                        events.Enqueue(new ChatEvent(EventType.Retry, "Twitch requested a reconnect."));
                        return;
                    }
                    else if (command == "NOTICE" && !joined)
                    {
                        string error = authenticated
                            ? "Twitch rejected the channel join. Check the channel name and account access."
                            : "Twitch rejected the login. Check the account and its chat:read token.";
                        events.Enqueue(new ChatEvent(EventType.Error, error));
                        return;
                    }
                    else if (command == "PART" && arguments == channel &&
                        match.Groups["prefix"].Value.StartsWith(settings.login + "!", StringComparison.OrdinalIgnoreCase))
                    {
                        events.Enqueue(new ChatEvent(EventType.Error, "Twitch removed the chat account from the channel."));
                        return;
                    }
                }
            }

            if (!cancellation.IsCancellationRequested)
                events.Enqueue(new ChatEvent(EventType.Retry, "The Twitch connection closed."));
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            events.Enqueue(authenticated
                ? new ChatEvent(EventType.Error, "Twitch did not confirm the channel. Check the channel name and account access.")
                : new ChatEvent(EventType.Retry, "The Twitch connection timed out."));
        }
        catch (PlatformNotSupportedException)
        {
            events.Enqueue(new ChatEvent(EventType.Error, "Twitch chat is unavailable on this platform."));
        }
        catch (Exception)
        {
            events.Enqueue(new ChatEvent(EventType.Retry, "Could not reach Twitch chat."));
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string line, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private static bool TryParseChat(Match line, string channel, out ChatEvent chat)
    {
        chat = default;
        string arguments = line.Groups["arguments"].Value;
        string messageStart = channel + " :";
        if (!arguments.StartsWith(messageStart, StringComparison.OrdinalIgnoreCase))
            return false;

        string prefix = line.Groups["prefix"].Value;
        int loginEnd = prefix.IndexOf('!');
        if (loginEnd <= 0)
            return false;

        string login = prefix.Substring(0, loginEnd);
        string displayName = login;
        string id = "";
        foreach (string tag in line.Groups["tags"].Value.Split(';'))
        {
            const string displayNameTag = "display-name=";
            if (tag.StartsWith(displayNameTag, StringComparison.Ordinal) && tag.Length > displayNameTag.Length)
                displayName = tag.Substring(displayNameTag.Length);
            else if (tag.StartsWith("id=", StringComparison.Ordinal))
                id = tag.Substring(3);
        }

        chat = new ChatEvent(EventType.Chat, arguments.Substring(messageStart.Length), login, displayName, id);
        return true;
    }
}
