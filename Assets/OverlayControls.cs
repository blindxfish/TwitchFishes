using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

public partial class TwitchChat
{
    [Serializable]
    private sealed class DashboardCommand
    {
        public string command = "";
        public string channel = "";
        public string login = "";
        public string token = "";
        public string clientId = "";
        public bool remember = false;
        public string rewardId = "";
        public int action = 0;
    }

    [Serializable]
    private sealed class DashboardState
    {
        public ChatStatus chat;
        public RewardStatus rewards;
        public SignInStatus signIn;
    }

    [Serializable]
    private sealed class SignInStatus
    {
        public string clientId;
        public string detail;
        public string verificationUrl;
        public string userCode;
        public bool hasClientId;
        public bool remember;
        public bool canRemember;
        public bool busy;
        public bool ownsConnection;
        public bool canCancel;
    }

    [Serializable]
    private sealed class ChatStatus
    {
        public string channel;
        public string login;
        public string summary;
        public string detail;
        public string error;
        public bool connected;
        public bool canDisconnect;
        public bool remember;
        public bool hasToken;
    }

    [Serializable]
    private sealed class RewardStatus
    {
        public string channel;
        public string detail;
        public string latest;
        public bool connected;
        public bool canDisconnect;
        public bool canRefresh;
        public bool remember;
        public bool hasToken;
        public RewardRow[] catalog;
    }

    [Serializable]
    private sealed class RewardRow
    {
        public string id;
        public string title;
        public int cost;
        public string availability;
        public int action;
    }

    private readonly ChatStatus chatStatus = new ChatStatus();
    private OverlayControlsServer dashboard;

    private void StartDashboard()
    {
        if (dashboard != null)
            return;

        TextAsset html = Resources.Load<TextAsset>("StreamDashboard/index");
        TextAsset css = Resources.Load<TextAsset>("StreamDashboard/styles");
        TextAsset js = Resources.Load<TextAsset>("StreamDashboard/dashboard");
        if (html == null || css == null || js == null)
        {
            Debug.LogWarning("TwitchFishes dashboard assets are unavailable.");
            return;
        }

        try
        {
            dashboard = new OverlayControlsServer(html.text, css.text, js.text);
            Debug.Log("TwitchFishes output is running. Press F2 in the aquarium window to open its controls in your browser.");
        }
        catch (Exception exception) when (exception is HttpListenerException or SocketException or IOException or
            InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            Debug.LogWarning("TwitchFishes could not start its local dashboard. Press F2 to retry.");
        }
    }

    private void OpenDashboard()
    {
        StartDashboard();
        if (dashboard != null)
            Application.OpenURL(dashboard.DashboardUrl);
    }

    private OverlayControlsServer.Reply HandleDashboardRequest(string body)
    {
        if (body != null)
        {
            DashboardCommand request;
            try { request = JsonUtility.FromJson<DashboardCommand>(body); }
            catch (ArgumentException) { return BadDashboardRequest(); }

            if (request == null || !ApplyDashboardCommand(request))
                return BadDashboardRequest();
        }

        chatStatus.channel = settings.channel;
        chatStatus.login = settings.login;
        chatStatus.remember = settings.rememberToken;
        chatStatus.hasToken = !signIn.OwnsConnection && !string.IsNullOrWhiteSpace(settings.token);
        var state = new DashboardState
        {
            chat = chatStatus,
            signIn = new SignInStatus
            {
                clientId = signIn.ClientId,
                detail = signIn.Detail,
                verificationUrl = signIn.VerificationUrl,
                userCode = signIn.UserCode,
                hasClientId = signIn.HasClientId,
                remember = signIn.Remember,
                canRemember = signIn.CanRemember,
                busy = signIn.Busy,
                ownsConnection = signIn.OwnsConnection,
                canCancel = signIn.CanCancel
            },
            rewards = new RewardStatus
            {
                channel = rewards.Channel,
                detail = rewards.SaveError == null ? rewards.Status : $"{rewards.Status} {rewards.SaveError}",
                latest = rewards.Latest,
                connected = rewards.Listening,
                canDisconnect = rewards.CanDisconnect,
                canRefresh = rewards.CanRefresh,
                remember = rewards.RememberToken,
                hasToken = !string.IsNullOrEmpty(rewards.SavedToken),
                catalog = rewards.Catalog.Select(reward => new RewardRow
                {
                    id = reward.id,
                    title = reward.title,
                    cost = reward.cost,
                    availability = !reward.is_enabled ? "Disabled" : reward.is_paused ? "Paused" : !reward.is_in_stock ? "Sold out" : "Enabled",
                    action = (int)rewards.GetAction(reward.id)
                }).ToArray()
            }
        };
        return new OverlayControlsServer.Reply(200, JsonUtility.ToJson(state));
    }

    private static OverlayControlsServer.Reply BadDashboardRequest() =>
        new OverlayControlsServer.Reply(400, "{\"error\":\"That dashboard action is unavailable.\"}");

    private bool ApplyDashboardCommand(DashboardCommand request)
    {
        switch (request.command)
        {
            case "beginTwitchSignIn":
                signIn.Begin(request.clientId, request.remember);
                break;
            case "cancelTwitchSignIn":
                signIn.Cancel();
                break;
            case "disconnectTwitch":
                signIn.SignOut();
                break;
            case "connectChat":
                if (signIn.OwnsConnection || signIn.Busy)
                    return false;
                string login = request.login?.Trim() ?? "";
                string token = request.token;
                if (string.IsNullOrWhiteSpace(token) && string.Equals(login, settings.login, StringComparison.OrdinalIgnoreCase))
                    token = settings.token;
                Connect(new TwitchSettings { channel = request.channel, login = login, token = token, rememberToken = request.remember });
                break;
            case "disconnectChat":
                if (signIn.OwnsConnection || signIn.Busy)
                    return false;
                Disconnect();
                break;
            case "connectRewards":
                if (signIn.OwnsConnection || signIn.Busy)
                    return false;
                string rewardToken = string.IsNullOrWhiteSpace(request.token) ? rewards.SavedToken : request.token;
                rewards.Connect(rewardToken, request.remember, settings);
                break;
            case "disconnectRewards":
                if (signIn.OwnsConnection || signIn.Busy)
                    return false;
                rewards.Disconnect();
                break;
            case "refreshRewards":
                rewards.Refresh();
                break;
            case "setRewardAction":
                if (!Enum.IsDefined(typeof(TwitchRewardAction), request.action) || !rewards.Catalog.Any(reward => reward.id == request.rewardId))
                    return false;
                rewards.SetAction(request.rewardId, (TwitchRewardAction)request.action);
                break;
            case "previewReward":
                if (!rewards.Catalog.Any(reward => reward.id == request.rewardId))
                    return false;
                rewards.TestAction(request.rewardId);
                break;
            case "demoFish":
                SimulateChatMessage();
                break;
            case "dropFood":
                makeFood();
                break;
            default:
                return false;
        }
        return true;
    }
}
