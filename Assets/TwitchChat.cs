using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class TwitchChat : MonoBehaviour
{
    [Header("Aquarium")]
    [SerializeField] private Water water;
    [SerializeField] private BoxCollider2D spawnArea;
    [SerializeField] private BoxCollider2D dolphinSpawnArea;
    [SerializeField] private Fish fishPrefab;
    [SerializeField] private GameObject foodPrefab;
    [SerializeField] private Dolphin dolphinPrefab;
    [SerializeField] private GameObject dissapDolphPrefab;
    [SerializeField] private Sprite[] fishSprites;
    [SerializeField] private GameObject topFishCanvas;

    private const float FishLifetime = 900f;
    private const float DolphinCooldown = 60f;
    private static readonly char[] CommandSeparators = { ' ', '\t' };

    private sealed class Viewer
    {
        public Fish Fish;
        public string DisplayName;
        public float ExpiresAt;
        public int Score;
    }

    private readonly Dictionary<string, Viewer> viewers = new Dictionary<string, Viewer>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> recentMessages = new HashSet<string>();
    private readonly Queue<string> messageOrder = new Queue<string>();
    private readonly List<string> expiredViewers = new List<string>();
    private TwitchSettings settings;
    private TwitchSettings manualSettings;
    private TwitchIrcConnection connection;
    private TwitchRewards rewards;
    private TwitchDeviceSignIn signIn;
    private TMP_Text topFishText;
    private Coroutine hideScores;
    private string currentChannel;
    private string saveError;
    private float retryAt = float.PositiveInfinity;
    private float retryDelay = 2f;
    private float nextDolphinTime;
    private int demoCount;

    private void Start()
    {
        Application.runInBackground = true;
        topFishText = topFishCanvas.GetComponentInChildren<TMP_Text>(true);
        topFishText.richText = false;
        topFishCanvas.SetActive(false);

        string loadError = null;
        try
        {
            settings = TwitchSettings.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            settings = new TwitchSettings();
            loadError = "Saved Twitch settings could not be read. Enter your settings again.";
        }

        manualSettings = settings;
        rewards = new TwitchRewards(HandleReward);
        signIn = new TwitchDeviceSignIn(ConnectSignedInAccount, DisconnectSignedInAccount);
        SetStatus("Offline", loadError ?? "Connect with Twitch to read your channel’s chat.", false, false);
        StartDashboard();
        if (!signIn.OwnsConnection)
            rewards.Restore(settings);
        if (!signIn.OwnsConnection && settings.autoConnect)
        {
            if (settings.Normalize(out string error))
            {
                currentChannel = settings.channel;
                StartConnection();
            }
            else
                SetStatus("Check settings", error, false, false);
        }

        StartCoroutine(RemoveInactiveFish());
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F2))
            OpenDashboard();

        signIn.Update();
        rewards.Update();
        if (Time.unscaledTime >= retryAt)
            StartConnection();

        for (int i = 0; i < 128 && connection != null && connection.TryRead(out var chatEvent); i++)
        {
            switch (chatEvent.Type)
            {
                case TwitchIrcConnection.EventType.Joined:
                    retryDelay = 2f;
                    SetStatus($"Live · {chatEvent.Text}", $"Connected to {chatEvent.Text}.", true, true);
                    break;
                case TwitchIrcConnection.EventType.Chat:
                    if (IsNewMessage(chatEvent.Id))
                        HandleChat(chatEvent.Login, chatEvent.DisplayName, chatEvent.Text);
                    break;
                case TwitchIrcConnection.EventType.Retry:
                    CloseConnection();
                    retryAt = Time.unscaledTime + retryDelay;
                    retryDelay = Mathf.Min(retryDelay * 2f, 30f);
                    SetStatus("Reconnecting", $"{chatEvent.Text} Retrying automatically.", false, true);
                    break;
                case TwitchIrcConnection.EventType.Error:
                    CloseConnection();
                    SetStatus("Check settings", chatEvent.Text, false, false);
                    break;
            }
        }

        dashboard?.Update(HandleDashboardRequest);
    }

    private void Connect(TwitchSettings requested, bool fromSignIn = false)
    {
        if (!requested.Normalize(out string error))
        {
            chatStatus.error = error;
            return;
        }

        rewards.ChatChannelChanged(requested.channel, persistManualSettings: !fromSignIn);
        if (currentChannel != null && currentChannel != requested.channel)
        {
            foreach (Viewer viewer in viewers.Values)
                if (viewer.Fish != null)
                    Destroy(viewer.Fish.gameObject);
            viewers.Clear();
            recentMessages.Clear();
            messageOrder.Clear();
            topFishCanvas.SetActive(false);
        }

        settings = requested;
        currentChannel = settings.channel;
        if (fromSignIn)
            saveError = null;
        else
        {
            manualSettings = settings;
            settings.autoConnect = true;
            SaveSettings();
        }
        retryDelay = 2f;
        StartConnection();
    }

    private void ConnectSignedInAccount(string login, string token)
    {
        Connect(new TwitchSettings { channel = login, login = login, token = token }, fromSignIn: true);
        rewards.Connect("", false, settings, fromSignIn: true);
    }

    private void DisconnectSignedInAccount()
    {
        CloseConnection();
        rewards.Disconnect(persistManualSettings: false);
        settings = manualSettings;
        SetStatus("Offline", "Disconnected from Twitch.", false, false);
    }

    private void Disconnect()
    {
        CloseConnection();
        settings.autoConnect = false;
        SaveSettings();
        SetStatus("Offline", "Disconnected from Twitch chat.", false, false);
    }

    private void SaveSettings()
    {
        saveError = null;
        try
        {
            settings.Save();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            saveError = "Settings could not be saved on this device.";
        }
    }

    private void SetStatus(string summary, string detail, bool connected, bool canDisconnect)
    {
        chatStatus.summary = summary;
        chatStatus.detail = saveError == null ? detail : $"{detail} {saveError}";
        chatStatus.connected = connected;
        chatStatus.canDisconnect = canDisconnect;
        chatStatus.error = null;
    }

    private void StartConnection()
    {
        CloseConnection();
        connection = new TwitchIrcConnection(settings);
        SetStatus("Connecting", $"Connecting to #{settings.channel}…", false, true);
    }

    private void CloseConnection()
    {
        retryAt = float.PositiveInfinity;
        connection?.Dispose();
        connection = null;
    }

    private void OnDestroy()
    {
        dashboard?.Dispose();
        signIn?.Dispose();
        rewards?.Dispose();
        CloseConnection();
    }

    private bool IsNewMessage(string id)
    {
        if (string.IsNullOrEmpty(id))
            return true;
        if (!recentMessages.Add(id))
            return false;
        messageOrder.Enqueue(id);
        if (messageOrder.Count > 512)
            recentMessages.Remove(messageOrder.Dequeue());
        return true;
    }

    private void HandleChat(string login, string displayName, string message)
    {
        EnsureViewer(login, displayName);
        string command = message.TrimStart();
        int end = command.IndexOfAny(CommandSeparators);
        if (end >= 0)
            command = command.Substring(0, end);

        switch (command.ToLowerInvariant())
        {
            case "!food": makeFood(); break;
            case "!topfish": DisplayTopFish(); break;
            case "!dolphin": RequestDolphin(); break;
        }
    }

    private void HandleReward(TwitchRewardAction action, string login, string displayName)
    {
        EnsureViewer(login, displayName);
        switch (action)
        {
            case TwitchRewardAction.DropFood: makeFood(); break;
            case TwitchRewardAction.TopFish: DisplayTopFish(); break;
            case TwitchRewardAction.CallDolphin: RequestDolphin(); break;
        }
    }

    private void EnsureViewer(string login, string displayName)
    {
        if (!viewers.TryGetValue(login, out Viewer viewer))
        {
            viewer = new Viewer();
            viewers.Add(login, viewer);
        }

        viewer.DisplayName = displayName;
        viewer.ExpiresAt = Time.unscaledTime + FishLifetime;
        if (viewer.Fish == null)
        {
            viewer.Fish = Instantiate(fishPrefab);
            Sprite appearance = fishSprites.Length == 0 ? null : fishSprites[Random.Range(0, fishSprites.Length)];
            viewer.Fish.Initialize(this, water, spawnArea, login, displayName, appearance);
        }
        else if (viewer.Fish.DisplayName != displayName)
        {
            viewer.Fish.SetDisplayName(displayName);
        }
    }

    public void AddScore(Fish fish, int change)
    {
        if (viewers.TryGetValue(fish.Login, out Viewer viewer) && viewer.Fish == fish)
            viewer.Score += change;
    }

    public void makeFood()
    {
        Instantiate(foodPrefab, RandomSpawnPoint(spawnArea, -1f), Quaternion.identity);
    }

    public void SimulateChatMessage()
    {
        int number = ++demoCount;
        HandleChat($"demo:{number}", $"Demo fish {number}", "");
    }

    private void RequestDolphin()
    {
        if (Time.time < nextDolphinTime)
        {
            var disappointed = Instantiate(dissapDolphPrefab, new Vector3(-17f, -7f, -2f), Quaternion.identity);
            Destroy(disappointed, 3f);
            return;
        }

        nextDolphinTime = Time.time + DolphinCooldown;
        var candidates = viewers.Values.Where(viewer => viewer.Fish != null && viewer.Fish.IsSwimming).ToList();
        Fish target = candidates.Count == 0 ? null : candidates[Random.Range(0, candidates.Count)].Fish;
        Dolphin dolphin = Instantiate(dolphinPrefab, RandomSpawnPoint(dolphinSpawnArea, -2f), Quaternion.identity);
        dolphin.Hunt(target);
    }

    private void DisplayTopFish()
    {
        var leaders = viewers.Values.OrderByDescending(viewer => viewer.Score)
            .ThenBy(viewer => viewer.DisplayName, StringComparer.OrdinalIgnoreCase).Take(5);
        topFishText.text = "Top fishes\n" + string.Join("\n", leaders.Select(viewer => $"{viewer.DisplayName}  {viewer.Score}"));
        topFishCanvas.SetActive(true);

        if (hideScores != null)
            StopCoroutine(hideScores);
        hideScores = StartCoroutine(HideScores());
    }

    private IEnumerator HideScores()
    {
        yield return new WaitForSeconds(5f);
        topFishCanvas.SetActive(false);
        hideScores = null;
    }

    private IEnumerator RemoveInactiveFish()
    {
        var wait = new WaitForSecondsRealtime(1f);
        while (true)
        {
            yield return wait;
            foreach (var entry in viewers)
            {
                Viewer viewer = entry.Value;
                if (viewer.ExpiresAt > Time.unscaledTime)
                    continue;
                if (viewer.Fish != null)
                {
                    Destroy(viewer.Fish.gameObject);
                    viewer.Fish = null;
                }
                if (viewer.Score == 0)
                    expiredViewers.Add(entry.Key);
            }

            foreach (string login in expiredViewers)
                viewers.Remove(login);
            expiredViewers.Clear();
        }
    }

    private static Vector3 RandomSpawnPoint(BoxCollider2D area, float z)
    {
        Bounds bounds = area.bounds;
        return new Vector3(Random.Range(bounds.min.x, bounds.max.x), Random.Range(bounds.min.y, bounds.max.y), z);
    }
}
