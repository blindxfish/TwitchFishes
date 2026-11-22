using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

internal sealed class TwitchDeviceSignIn : IDisposable
{
    private const string OAuth = "https://id.twitch.tv/oauth2/";
    private const string Scopes = "chat:read channel:read:redemptions";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private static readonly Regex ClientIdPattern = new Regex(@"\A[a-zA-Z0-9]{20,64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex LoginPattern = new Regex(@"\A[a-z0-9_]{1,25}\z", RegexOptions.CultureInvariant);
    private static readonly HttpClient Twitch = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    [Serializable]
    private sealed class Preferences
    {
        public string clientId = "";
        public string protectedSession = "";
    }

    [Serializable]
    private sealed class Session
    {
        public string login;
        public string accessToken;
        public string refreshToken;
        public long expiresAt;
    }

    [Serializable]
    private sealed class DeviceCode
    {
        public string device_code = "";
        public string user_code = "";
        public string verification_uri = "";
        public int expires_in = 0;
        public int interval = 0;
    }

    [Serializable]
    private sealed class TokenResponse
    {
        public string access_token = "";
        public string refresh_token = "";
        public int expires_in = 0;
        public string message = "";
        public string error = "";
    }

    [Serializable]
    private sealed class TokenIdentity
    {
        public string client_id = "";
        public string user_id = "";
        public string login = "";
        public string[] scopes = null;
        public int expires_in = 0;
    }

    private sealed class SignInError : Exception
    {
        public SignInError(string message) : base(message) { }
    }

    private readonly HttpClient http;
    private readonly Action<string, string> connect;
    private readonly Action disconnect;
    private readonly string path;
    private readonly Preferences preferences;
    private readonly FileStream storageLock;
    private Task<Session> operation;
    private CancellationTokenSource cancellation;
    private volatile DeviceCode challenge;
    private Session session;
    private bool authorizing;
    private bool rememberAttempt;
    private bool connected;
    private long validateAt;

    public TwitchDeviceSignIn(Action<string, string> connect, Action disconnect, HttpClient http = null, string settingsPath = null)
    {
        this.connect = connect;
        this.disconnect = disconnect;
        this.http = http ?? Twitch;
        path = settingsPath ?? Path.Combine(Application.persistentDataPath, "twitch-sign-in.json");
        if (IsWindows)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                storageLock = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        try
        {
            preferences = File.Exists(path)
                ? JsonUtility.FromJson<Preferences>(File.ReadAllText(path)) ?? new Preferences()
                : new Preferences();
            if (CanRemember && HasClientId && !string.IsNullOrEmpty(preferences.protectedSession))
            {
                session = JsonUtility.FromJson<Session>(TwitchSessionProtection.Unprotect(preferences.protectedSession));
                if (session == null || !IsLogin(session.login) || string.IsNullOrEmpty(session.refreshToken))
                    throw new InvalidDataException("Invalid saved Twitch sign-in.");
                Remember = true;
                Detail = "Restoring your Twitch sign-in…";
                StartMaintenance();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            preferences ??= new Preferences();
            session = null;
            Detail = "The saved Twitch sign-in could not be opened. Connect with Twitch again.";
        }
    }

    public string ClientId => preferences.clientId ?? "";
    public bool HasClientId => ClientIdPattern.IsMatch(ClientId);
    private static bool IsWindows => Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
    public bool CanRemember => storageLock != null;
    public bool Remember { get; private set; }
    public bool OwnsConnection => session != null;
    public bool Busy => operation != null;
    public bool CanCancel => authorizing && Busy && !cancellation.IsCancellationRequested;
    public string VerificationUrl => !CanCancel ? null : challenge?.verification_uri;
    public string UserCode => !CanCancel ? null : challenge?.user_code;
    public string Detail { get; private set; } = "Connect your broadcaster account for chat and Channel Points rewards.";

    public void Begin(string clientId, bool remember)
    {
        if (Busy || OwnsConnection)
            return;

        string candidate = (clientId ?? "").Trim();
        if (!ClientIdPattern.IsMatch(candidate))
        {
            Detail = "Enter the Client ID of a Public application from the Twitch Developer Console.";
            return;
        }

        rememberAttempt = remember && CanRemember;
        authorizing = true;
        challenge = null;
        Detail = "Requesting a sign-in code from Twitch…";
        cancellation = new CancellationTokenSource();
        operation = AuthorizeAsync(candidate, cancellation.Token);
    }

    public void Cancel()
    {
        if (CanCancel)
        {
            cancellation.Cancel();
            challenge = null;
            Detail = "Twitch sign-in canceled.";
        }
    }

    public void SignOut()
    {
        if (!OwnsConnection)
            return;

        cancellation?.Cancel();
        ClearSession();
        Detail = "Disconnected from Twitch.";
    }

    public void Update()
    {
        if (operation?.IsCompleted == true)
            FinishOperation();

        if (operation == null && session != null && Now >= validateAt)
            StartMaintenance();
    }

    public void Dispose()
    {
        storageLock?.Dispose();
        if (cancellation == null)
            return;

        CancellationTokenSource pendingCancellation = cancellation;
        pendingCancellation.Cancel();
        _ = operation.ContinueWith(task =>
        {
            _ = task.Exception;
            pendingCancellation.Dispose();
        }, TaskScheduler.Default);
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool IsLogin(string login) => login != null && LoginPattern.IsMatch(login);

    private string authorizedClientId;

    private async Task<Session> AuthorizeAsync(string candidate, CancellationToken token)
    {
        using var deviceResponse = await Post("device", token, ("client_id", candidate), ("scopes", Scopes)).ConfigureAwait(false);
        if (!deviceResponse.IsSuccessStatusCode)
            throw new SignInError("Twitch rejected the app. Check the Client ID and set the application's Client Type to Public.");

        DeviceCode device = JsonUtility.FromJson<DeviceCode>(await deviceResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (string.IsNullOrEmpty(device?.device_code) || string.IsNullOrEmpty(device.user_code) ||
            device.expires_in <= 0 || !IsTwitchActivationPage(device.verification_uri))
            throw new InvalidDataException("Twitch returned an invalid activation code.");

        token.ThrowIfCancellationRequested();
        challenge = device;
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, device.interval));
        long expires = Now + device.expires_in;
        while (Now < expires)
        {
            await Task.Delay(interval, token).ConfigureAwait(false);
            if (Now >= expires)
                break;

            using var response = await Post("token", token, ("client_id", candidate), ("scopes", Scopes),
                ("device_code", device.device_code), ("grant_type", DeviceGrant)).ConfigureAwait(false);
            TokenResponse grant = await ReadTokens(response).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var approved = MakeSession(grant, "");
                TokenIdentity identity = await ValidateApprovedAsync(candidate, approved.accessToken, token).ConfigureAwait(false);
                if (identity == null)
                    throw new SignInError("Twitch could not validate the approved sign-in. Try connecting again.");

                approved.login = identity.login;
                approved.expiresAt = Now + identity.expires_in;
                authorizedClientId = candidate;
                return approved;
            }

            string error = string.IsNullOrEmpty(grant.error) ? grant.message : grant.error;
            if (error == "authorization_pending")
                continue;
            if (error == "slow_down" || response.StatusCode == (HttpStatusCode)429)
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }
            if ((int)response.StatusCode >= 500)
                continue;
            if (error == "access_denied")
                throw new SignInError("Twitch sign-in was declined.");
            if (error == "expired_token" || error == "invalid device code")
                break;

            response.EnsureSuccessStatusCode();
        }
        throw new SignInError("The Twitch code expired. Connect again to get a new code.");
    }

    private static bool IsTwitchActivationPage(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        (uri.Host == "www.twitch.tv" || uri.Host == "twitch.tv") &&
        uri.AbsolutePath.TrimEnd('/') == "/activate" && uri.UserInfo.Length == 0;

    private async Task<TokenIdentity> ValidateApprovedAsync(string clientId, string accessToken, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await ValidateAsync(clientId, accessToken, token).ConfigureAwait(false); }
            catch (Exception error) when ((error is HttpRequestException || error is TaskCanceledException) &&
                !token.IsCancellationRequested && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
        }
    }

    private void StartMaintenance()
    {
        cancellation = new CancellationTokenSource();
        operation = ValidateOrRefreshAsync(ClientId, session, cancellation.Token);
    }

    private async Task<Session> ValidateOrRefreshAsync(string clientId, Session previous, CancellationToken token)
    {
        TokenIdentity identity = await ValidateAsync(clientId, previous.accessToken, token).ConfigureAwait(false);
        if (identity != null)
        {
            if (!string.Equals(identity.login, previous.login, StringComparison.OrdinalIgnoreCase))
                throw new SignInError("The saved account no longer matches this token. Connect with Twitch again.");

            if (identity.expires_in > 60)
            {
                previous.expiresAt = Now + identity.expires_in;
                return previous;
            }
        }

        using var response = await Post("token", token, ("client_id", clientId), ("grant_type", "refresh_token"),
            ("refresh_token", previous.refreshToken)).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
            throw new SignInError("Your Twitch sign-in expired or was revoked. Connect with Twitch again.");

        response.EnsureSuccessStatusCode();
        return MakeSession(await ReadTokens(response).ConfigureAwait(false), previous.login);
    }

    private async Task<TokenIdentity> ValidateAsync(string clientId, string accessToken, CancellationToken token)
    {
        if (string.IsNullOrEmpty(accessToken))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, OAuth + "validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        response.EnsureSuccessStatusCode();
        var identity = JsonUtility.FromJson<TokenIdentity>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (identity?.client_id != clientId || string.IsNullOrEmpty(identity.user_id) || !IsLogin(identity.login) ||
            identity.scopes == null || Array.IndexOf(identity.scopes, "chat:read") < 0 ||
            Array.IndexOf(identity.scopes, "channel:read:redemptions") < 0)
            throw new SignInError("Authorize both chat and Channel Points access with your broadcaster account.");

        return identity;
    }

    private async Task<HttpResponseMessage> Post(string endpoint, CancellationToken token, params (string Key, string Value)[] fields)
    {
        var form = new List<KeyValuePair<string, string>>(fields.Length);
        foreach (var field in fields)
            form.Add(new KeyValuePair<string, string>(field.Key, field.Value));
        using var request = new HttpRequestMessage(HttpMethod.Post, OAuth + endpoint) { Content = new FormUrlEncodedContent(form) };
        return await http.SendAsync(request, token).ConfigureAwait(false);
    }

    private static async Task<TokenResponse> ReadTokens(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return new TokenResponse();
        try { return JsonUtility.FromJson<TokenResponse>(json) ?? new TokenResponse(); }
        catch (ArgumentException) when (!response.IsSuccessStatusCode) { return new TokenResponse(); }
    }

    private static Session MakeSession(TokenResponse grant, string login)
    {
        if (!TwitchSettings.IsTokenFormatValid(grant.access_token ?? "") || string.IsNullOrEmpty(grant.refresh_token) ||
            grant.expires_in <= 0)
            throw new InvalidDataException("Twitch returned an invalid token response.");

        return new Session { login = login, accessToken = grant.access_token, refreshToken = grant.refresh_token, expiresAt = Now + grant.expires_in };
    }

    private void FinishOperation()
    {
        bool wasCanceled = cancellation.IsCancellationRequested;
        bool wasAuthorizing = authorizing;
        Task<Session> completed = operation;
        cancellation.Dispose();
        cancellation = null;
        operation = null;
        authorizing = false;
        challenge = null;
        if (wasCanceled)
        {
            _ = completed.Exception;
            return;
        }

        try
        {
            Session result = completed.GetAwaiter().GetResult();
            if (wasAuthorizing)
            {
                preferences.clientId = authorizedClientId;
                Remember = rememberAttempt;
            }

            bool renewed = !wasAuthorizing && session.accessToken != result.accessToken;
            bool changed = !connected || renewed;
            session = result;
            validateAt = renewed ? Now + 5 : Math.Max(Now + 5, Math.Min(Now + 3500, result.expiresAt - 60));
            Detail = $"Signed in as {session.login}.";
            if (changed)
            {
                SaveSession();
                connect(session.login, session.accessToken);
                connected = true;
            }
        }
        catch (SignInError error)
        {
            if (!wasAuthorizing)
                ClearSession();
            Detail = error.Message;
        }
        catch (Exception)
        {
            validateAt = Now + 30;
            Detail = wasAuthorizing ? "Twitch sign-in was interrupted. Try connecting again."
                : "Twitch sign-in check was interrupted. Retrying automatically.";
        }
    }

    private void ClearSession()
    {
        session = null;
        Remember = false;
        SaveSession();
        if (connected)
            disconnect();
        connected = false;
    }

    private void SaveSession()
    {
        if (IsWindows && storageLock == null)
            return;

        try
        {
            preferences.protectedSession = Remember && session != null
                ? TwitchSessionProtection.Protect(JsonUtility.ToJson(session))
                : "";
            TwitchSettings.SaveLocalJson(path, JsonUtility.ToJson(preferences, true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Detail += " This sign-in could not be saved locally.";
        }
    }
}
