using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

internal enum TwitchRewardAction { None, JoinAquarium, DropFood, TopFish, CallDolphin }

internal sealed class TwitchRewards : IDisposable
{
    [Serializable]
    private sealed class Binding
    {
        public string broadcasterId;
        public string rewardId;
        public TwitchRewardAction action;
    }

    [Serializable]
    private sealed class Preferences
    {
        public string token = "";
        public bool remember;
        public bool autoConnect;
        public List<Binding> bindings = new List<Binding>();
    }

    private static string SettingsPath => Path.Combine(Application.persistentDataPath, "twitch-rewards.json");

    private readonly Action<TwitchRewardAction, string, string> runAction;
    private readonly HashSet<string> recent = new HashSet<string>(StringComparer.Ordinal);
    private readonly Queue<string> recentOrder = new Queue<string>();
    private readonly Preferences preferences;
    private TwitchRewardConnection connection;
    private string broadcasterId;
    private string selectedChannel;
    private string suppliedToken;
    private bool rememberSuppliedToken;
    private bool signedInConnection;
    private bool canRefresh;
    private string saveError;

    public TwitchRewards(Action<TwitchRewardAction, string, string> runAction)
    {
        this.runAction = runAction;
        try
        {
            preferences = File.Exists(SettingsPath)
                ? JsonUtility.FromJson<Preferences>(File.ReadAllText(SettingsPath)) ?? new Preferences()
                : new Preferences();
            preferences.bindings ??= new List<Binding>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            preferences = new Preferences();
            Status = "Saved reward settings could not be loaded.";
        }
    }

    public TwitchRewardDefinition[] Catalog { get; private set; } = Array.Empty<TwitchRewardDefinition>();
    public string Status { get; private set; } = "Connect with Twitch as the broadcaster to load custom rewards.";
    public string Channel { get; private set; }
    public string Latest { get; private set; }
    public bool Listening { get; private set; }
    public bool CanDisconnect => connection != null;
    public bool CanRefresh => canRefresh && connection != null;
    public string SavedToken => preferences.token;
    public bool RememberToken => preferences.remember;
    public string SaveError => saveError;

    public void Restore(TwitchSettings chat)
    {
        if (preferences.autoConnect && (!string.IsNullOrWhiteSpace(preferences.token) || !string.IsNullOrWhiteSpace(chat.token)))
            Connect(preferences.token, preferences.remember, chat);
    }

    public void Connect(string rawToken, bool remember, TwitchSettings chat, bool fromSignIn = false)
    {
        string separateToken = TwitchSettings.NormalizeToken(rawToken);
        string token = string.IsNullOrEmpty(separateToken) ? TwitchSettings.NormalizeToken(chat.token) : separateToken;
        if (!TwitchSettings.IsTokenFormatValid(token))
        {
            Status = "Enter a broadcaster token with channel:read:redemptions or connect chat using that token.";
            return;
        }

        StopConnection();
        canRefresh = false;
        suppliedToken = separateToken;
        rememberSuppliedToken = remember && separateToken.Length > 0;
        signedInConnection = fromSignIn;
        selectedChannel = chat.channel?.Trim();
        if (!string.IsNullOrEmpty(selectedChannel) && Channel != selectedChannel)
            ClearCatalog();

        connection = new TwitchRewardConnection(token, selectedChannel);
        Status = "Checking the broadcaster token and loading rewards…";
    }

    public void Disconnect(bool persistManualSettings = true)
    {
        StopConnection();
        if (persistManualSettings)
        {
            preferences.autoConnect = false;
            Save();
        }
        Status = "Reward listener stopped.";
    }

    public void ChatChannelChanged(string channel, bool persistManualSettings = true)
    {
        if ((Channel ?? selectedChannel) == channel || (connection == null && Channel == null))
            return;

        StopConnection();
        ClearCatalog();
        if (persistManualSettings)
        {
            preferences.autoConnect = false;
            Save();
        }
        Status = $"Connect rewards for #{channel}.";
    }

    public void Refresh()
    {
        if (connection?.Refresh() != true)
            return;

        canRefresh = false;
        Status = "Refreshing custom rewards…";
    }

    public void Update()
    {
        for (int i = 0; i < 128 && connection != null && connection.TryRead(out var message); i++)
        {
            switch (message.Type)
            {
                case TwitchRewardConnection.EventType.Catalog:
                    if (broadcasterId != message.BroadcasterId)
                        ClearCatalog();
                    broadcasterId = message.BroadcasterId;
                    Channel = message.Channel;
                    Catalog = message.Catalog.Where(reward => !string.IsNullOrEmpty(reward?.id))
                        .OrderByDescending(reward => reward.is_enabled && !reward.is_paused)
                        .ThenBy(reward => reward.title, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    canRefresh = true;
                    Status = Listening ? $"Listening for rewards in #{Channel}." : $"Connecting the reward listener for #{Channel}…";
                    break;
                case TwitchRewardConnection.EventType.Listening:
                    Listening = true;
                    if (!signedInConnection)
                    {
                        preferences.remember = rememberSuppliedToken;
                        preferences.token = rememberSuppliedToken ? suppliedToken : "";
                        preferences.autoConnect = rememberSuppliedToken || suppliedToken.Length == 0;
                        Save();
                    }
                    Status = $"Listening for rewards in #{message.Channel}.";
                    break;
                case TwitchRewardConnection.EventType.Retrying:
                    Listening = false;
                    Status = message.Detail;
                    break;
                case TwitchRewardConnection.EventType.RefreshFailed:
                    canRefresh = true;
                    Status = message.Detail;
                    break;
                case TwitchRewardConnection.EventType.Error:
                    StopConnection();
                    Status = message.Detail;
                    break;
                case TwitchRewardConnection.EventType.Redemption:
                    ApplyRedemption(message.Redemption);
                    break;
            }
        }
    }

    public TwitchRewardAction GetAction(string rewardId)
    {
        TwitchRewardAction selected = FindBinding(rewardId)?.action ?? TwitchRewardAction.None;
        return Enum.IsDefined(typeof(TwitchRewardAction), selected) ? selected : TwitchRewardAction.None;
    }

    public void SetAction(string rewardId, TwitchRewardAction action)
    {
        if (!Catalog.Any(reward => reward.id == rewardId))
            return;

        Binding binding = FindBinding(rewardId);
        if (action == TwitchRewardAction.None)
        {
            if (binding != null)
                preferences.bindings.Remove(binding);
        }
        else if (binding != null)
            binding.action = action;
        else
            preferences.bindings.Add(new Binding { broadcasterId = broadcasterId, rewardId = rewardId, action = action });

        Save();
    }

    public void TestAction(string rewardId)
    {
        TwitchRewardAction action = GetAction(rewardId);
        if (action == TwitchRewardAction.None)
            return;
        runAction(action, "demo:reward", "Reward demo");
        Latest = $"Preview ran {ActionLabel(action)} in the aquarium.";
    }

    public static string ActionLabel(TwitchRewardAction action)
    {
        return action switch
        {
            TwitchRewardAction.JoinAquarium => "Join aquarium",
            TwitchRewardAction.DropFood => "Drop food",
            TwitchRewardAction.TopFish => "Show top fish",
            TwitchRewardAction.CallDolphin => "Call dolphin",
            _ => "No action"
        };
    }

    private void ApplyRedemption(TwitchRedemption redemption)
    {
        if (redemption.broadcaster_user_id != broadcasterId || !recent.Add(redemption.id))
            return;
        recentOrder.Enqueue(redemption.id);
        if (recentOrder.Count > 1024)
            recent.Remove(recentOrder.Dequeue());

        TwitchRewardAction action = GetAction(redemption.reward.id);
        string viewer = string.IsNullOrEmpty(redemption.user_name) ? redemption.user_login : redemption.user_name;
        Latest = $"{viewer} redeemed {redemption.reward.title} · {ActionLabel(action)}";
        if (action != TwitchRewardAction.None)
            runAction(action, redemption.user_login, viewer);
    }

    private Binding FindBinding(string rewardId) =>
        preferences.bindings.Find(binding => binding != null && binding.broadcasterId == broadcasterId && binding.rewardId == rewardId);

    private void Save()
    {
        try
        {
            TwitchSettings.SaveLocalJson(SettingsPath, JsonUtility.ToJson(preferences, true));
            saveError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            saveError = "Reward settings could not be saved on this device.";
        }
    }

    private void ClearCatalog()
    {
        Channel = null;
        broadcasterId = null;
        Catalog = Array.Empty<TwitchRewardDefinition>();
        Latest = null;
        recent.Clear();
        recentOrder.Clear();
    }

    private void StopConnection()
    {
        connection?.Dispose();
        connection = null;
        Listening = false;
        canRefresh = false;
    }

    public void Dispose() => StopConnection();
}
