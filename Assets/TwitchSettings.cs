using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

[Serializable]
internal sealed class TwitchSettings
{
    private static readonly Regex LoginPattern = new Regex(@"\A[a-z0-9_]{1,25}\z", RegexOptions.CultureInvariant);
    private static readonly Regex TokenPattern = new Regex(@"\A[A-Za-z0-9._~-]{1,512}\z", RegexOptions.CultureInvariant);

    public string channel = "";
    public string login = "";
    public string token = "";
    public bool rememberToken;
    public bool autoConnect;

    private static string SettingsPath => Path.Combine(Application.persistentDataPath, "twitch-settings.json");

    public bool Normalize(out string error)
    {
        channel = (channel ?? "").Trim().TrimStart('#').ToLowerInvariant();
        login = (login ?? "").Trim().ToLowerInvariant();
        token = NormalizeToken(token);

        if (!LoginPattern.IsMatch(channel))
            error = "Enter a Twitch channel name, such as #mychannel.";
        else if (!LoginPattern.IsMatch(login))
            error = "Enter the Twitch login that owns the OAuth token.";
        else if (!IsTokenFormatValid(token))
            error = "Enter a Twitch user access token with chat:read permission.";
        else
            error = null;

        return error == null;
    }

    public static string NormalizeToken(string value)
    {
        string token = (value ?? "").Trim();
        return token.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? token.Substring(6).Trim() : token;
    }

    public static bool IsTokenFormatValid(string token) => TokenPattern.IsMatch(token);

    public static TwitchSettings Load()
    {
        if (File.Exists(SettingsPath))
            return JsonUtility.FromJson<TwitchSettings>(File.ReadAllText(SettingsPath)) ?? new TwitchSettings();

        string legacyPath = Path.Combine(Application.streamingAssetsPath, "config.txt");
        var settings = new TwitchSettings();
        if (!File.Exists(legacyPath))
            return settings;

        foreach (string line in File.ReadLines(legacyPath))
        {
            int separator = line.IndexOf(':');
            if (separator < 0)
                continue;

            string value = line.Substring(separator + 1).Trim();
            if (value.Equals("<placeholder>", StringComparison.OrdinalIgnoreCase))
                continue;

            switch (line.Substring(0, separator).Trim().ToLowerInvariant())
            {
                case "channel": settings.channel = value; break;
                case "nick": settings.login = value; break;
                case "oauth": settings.token = value; break;
            }
        }

        settings.autoConnect = settings.Normalize(out _);
        return settings;
    }

    public void Save()
    {
        var saved = new TwitchSettings
        {
            channel = channel,
            login = login,
            token = rememberToken ? token : "",
            rememberToken = rememberToken,
            autoConnect = autoConnect && rememberToken
        };
        SaveLocalJson(SettingsPath, JsonUtility.ToJson(saved, true));
    }

    public static void SaveLocalJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string pending = Path.Combine(Path.GetDirectoryName(path), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(pending, json);
            if (File.Exists(path))
                File.Replace(pending, path, null);
            else
                File.Move(pending, path);
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }
}
