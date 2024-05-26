using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Il2CppBroccoliGames;
using TwitchLib.Api;
using TwitchLib.Api.Core.Enums;
using TwitchLib.Api.Helix.Models.Chat.GetChatters;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;
using ThreadState = System.Threading.ThreadState;
using Timer = System.Threading.Timer;
using TimeSpan = System.TimeSpan;

namespace OGOF;

public sealed partial class Mod : MelonMod
{
    private static Regex _rxSentenceCase = new(@"\b\w", RegexOptions.Compiled);

    private const string ConfigFileName = "ogof.json";
    private CancellationTokenSource _lifetime = new();

    private OgofConfig _config = new();
    //private Font _font;

    private TwitchAPI _twitchApi = new();
    private GUIStyle[] _guiStyles;

    private string? _currentTwitchUserId;

    private Thread _twitchSyncThread;
    private string? _twitchRefreshToken;
    private bool _twitchAuthorized;

    private object _namesLock = new();

    private Dictionary<string, (string? DisplayName, DateTime Queried)> _names
        = new(StringComparer.OrdinalIgnoreCase);

    private Timer _twitchTokenRefreshTimer;
    private HttpClient _http;
    private string? _twitchAuthPromptCode;

    public override void OnInitializeMelon()
    {
        MelonEvents.OnGUI.Subscribe(DrawGui, 100); // The higher the value, the lower the priority.
        MelonEvents.OnApplicationDefiniteQuit.Subscribe(() =>
        {
            _lifetime.Cancel();
            _twitchSyncThread.Interrupt();
        });
    }

    public override void OnLateInitializeMelon()
    {
        _http = new HttpClient();

        if (File.Exists(ConfigFileName))
        {
            var jsonText = File.ReadAllText(ConfigFileName);
            _config = JsonSerializer.Deserialize<OgofConfig>(jsonText)
                      ?? new OgofConfig();
            _config.ApplyDefaults();
        }

        var settings = _twitchApi.Settings;
        settings.ClientId = _config.ClientId;
        settings.Scopes = _twitchApiScopes.Split(' ')
            .Select(s =>
            {
                var scopeSig = string.Join('_', s.Split(':'));
                if (Enum.TryParse(scopeSig, true, out AuthScopes scope))
                    return scope;
                if (Enum.TryParse($"helix_{scopeSig}", true, out scope))
                    return scope;
                //throw new NotImplementedException(s);
                return (AuthScopes) (-1); //ffs outdated TwitchLib
            })
            .Where(a => (int) a != -1)
            .ToList();

        _guiStyles = new GUIStyle[_config.FontColors.Length];

        for (var i = 0; i < _config.FontColors.Length; i++)
        {
            if (!ColorUtility.TryParseHtmlString(_config.FontColors[i], out var color))
                color = Color.white;

            var style = new GUIStyle
            {
                //font = _font,
                fontSize = _config.FontSize,
                alignment = TextAnchor.MiddleCenter,
                normal = {textColor = color},
                fontStyle = FontStyle.Bold
            };

            _guiStyles[i] = style;
        }

        _twitchSyncThread = new Thread(TwitchSyncWorker)
        {
            Name = "Twitch Sync Worker",
            IsBackground = true
        };

        async ValueTask TwitchDeviceCodeFlowAuth()
        {
            var resp = await _http.PostAsync("https://id.twitch.tv/oauth2/device",
                new FormUrlEncodedContent([
                    KeyValuePair.Create("client_id", _config.ClientId),
                    KeyValuePair.Create("scope", _twitchApiScopes)
                ]));

            var json = await resp.Content.ReadAsStringAsync();
            var jsDoc = JsonDocument.Parse(json).RootElement;
            var message = jsDoc.Get<string?>("message")
                          ?? jsDoc.Get<string?>("error");

            switch (message)
            {
                case null:
                case "":
                    break;
                default:
                    LoggerInstance.Error($"Twitch DCF error: {message}\n{json}");
                    return;
            }

            var deviceCode = jsDoc.Get<string>("device_code");
            var userCode = jsDoc.Get<string>("user_code");
            var verificationUri = jsDoc.Get<string>("verification_uri");
            var expiresIn = jsDoc.Get<int>("expires_in");
            var interval = jsDoc.Get<int>("interval");
            var intervalMs = interval * 1000;

            _twitchAuthPromptCode = userCode;

            if (_config.OpenTwitchAuthInBrowser)
            {
                var verificationStartInfo = new ProcessStartInfo
                {
                    FileName = verificationUri,
                    UseShellExecute = true
                };

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    verificationStartInfo.FileName = verificationStartInfo.FileName
                        .Replace("&", "^&");

                Process.Start(verificationStartInfo)?.Dispose();
            }

            _twitchAuthorized = false;
            bool success = false;
            var started = DateTime.Now;
            var expirationDate = DateTime.Now + TimeSpan.FromSeconds(expiresIn);
            var log = LoggerInstance;
            do
            {
                await Task.Delay(intervalMs);
                resp = await _http.PostAsync("https://id.twitch.tv/oauth2/token",
                    new FormUrlEncodedContent([
                        KeyValuePair.Create("client_id", _config.ClientId),
                        KeyValuePair.Create("device_code", deviceCode),
                        KeyValuePair.Create("scope", _twitchApiScopes),
                        KeyValuePair.Create("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
                    ]));

                json = await resp.Content.ReadAsStringAsync();

                //log.Msg($"Response JSON: {json}");

                jsDoc = JsonDocument.Parse(json).RootElement;

                message = jsDoc.Get<string?>("message")
                          ?? jsDoc.Get<string?>("error");

                switch (message)
                {
                    case null:
                    case "":
                        break;
                    case "authorization_pending":
                        var elapsed = Math.Round((DateTime.Now - started).TotalSeconds);
                        log.Warning($"Twitch DCF authorization still pending after {elapsed}s");
                        continue;
                    case "slow_down":
                        interval += 5;
                        log.Warning($"Twitch DCF slow down, increasing refresh interval to {interval}s");
                        break;
                    default:
                        log.Error($"Twitch DCF error while polling for auth: {message}\n{json}");
                        _twitchAuthPromptCode = null;
                        return;
                }

                var status = jsDoc.Get<int>("status");
                switch (status)
                {
                    case > 300:
                        log.Error($"Twitch DCF auth failed: {json}");
                        continue;
                }

                var accessToken = jsDoc.Get<string?>("access_token");
                var refreshToken = jsDoc.Get<string?>("refresh_token");
                expiresIn = jsDoc.Get<int>("expires_in");

                if (accessToken is null)
                {
                    // abnormal response
                    log.Error($"Twitch DCF abnormal response: {json}");
                    continue;
                }

                _twitchAuthPromptCode = null;

                settings.AccessToken = accessToken;
                _twitchRefreshToken = refreshToken;

                success = true;
                _twitchAuthorized = true;
                if ((_twitchSyncThread.ThreadState & ThreadState.Unstarted) != 0)
                    _twitchSyncThread.Start(this);
                log.Msg("Twitch DCF auth successful");

                _twitchTokenRefreshTimer = new Timer(
                    RefreshWorker,
                    this,
                    TimeSpan.FromSeconds(Math.Max(5, expiresIn - 2.5)),
                    Timeout.InfiniteTimeSpan);
            } while (!_twitchAuthorized && DateTime.Now < expirationDate);

            if (success == false)
                log.Error("Twitch DCF auth failed");
        }

        //TwitchDeviceCodeFlowAuth().GetAwaiter().GetResult();
        ThreadPool.QueueUserWorkItem(_ => TwitchDeviceCodeFlowAuth().GetAwaiter().GetResult());
    }

    private static void RefreshWorker(object? o)
    {
        var self = (Mod) o!;
        if (self._twitchRefreshToken is null) return;

        static async ValueTask RefreshToken(Mod self)
        {
            var log = self.LoggerInstance;
            self._twitchAuthorized = false;

            log.Msg("Refreshing Twitch auth...");

            var refreshResp = await self._http.PostAsync("https://id.twitch.tv/oauth2/token",
                new FormUrlEncodedContent([
                    KeyValuePair.Create("client_id", self._config.ClientId),
                    KeyValuePair.Create("refresh_token", self._twitchRefreshToken),
                    KeyValuePair.Create("grant_type", "refresh_token")
                ]));

            var json = await refreshResp.Content.ReadAsStringAsync();
            var jsDoc = JsonDocument.Parse(json).RootElement;
            var error = jsDoc.Get<string?>("error")
                        ?? jsDoc.Get<string?>("message");
            switch (error)
            {
                case null:
                case "":
                    break;
                default:
                    log.Error($"Twitch auth refresh error: {error}");
                    return;
            }

            var newAccessToken = jsDoc.Get<string?>("access_token");
            var newRefreshToken = jsDoc.Get<string?>("refresh_token");
            var expiresIn = jsDoc.Get<int>("expires_in");
            var refreshTime = TimeSpan.FromSeconds(Math.Max(5, expiresIn - 2));

            log.Msg("Twitch auth refresh successful");
            self._twitchApi.Settings.AccessToken = newAccessToken;
            self._twitchRefreshToken = newRefreshToken;
            self._twitchTokenRefreshTimer.Change(refreshTime, Timeout.InfiniteTimeSpan);
            self._twitchAuthorized = true;
        }

        RefreshToken(self).GetAwaiter().GetResult();
    }

    private async ValueTask<string?> GetCurrentTwitchUserId()
    {
        var twitch = _twitchApi;
        var resp = await twitch.Helix.Users.GetUsersAsync();
        return resp.Users.FirstOrDefault()?.Id;
    }

    private static void TwitchSyncWorker(object? o)
    {
        var self = (Mod) o!;

        while (!self._twitchAuthorized)
            Thread.Sleep(1000);

        var cfg = self._config;
        ref var twitch = ref self._twitchApi;

        for (;;)
        {
            var settings = twitch.Settings;

            if (settings.ClientId is not null
                && settings.AccessToken is not null)
                break; // good to go

            Thread.Sleep(1000);
        }

        var broadcaster = cfg.BroadcasterId ?? (self._currentTwitchUserId
            ??= self.GetCurrentTwitchUserId().GetAwaiter().GetResult());
        if (broadcaster is null) return;

        var moderator = cfg.ModeratorId ?? broadcaster;

        do
        {
            async ValueTask AsyncWork()
            {
                var twitch = self._twitchApi;
                var (total, cursor, userLogins)
                    = await GetUserLogins(cfg, twitch, broadcaster, moderator);
                var pageCount = 1;

                void SyncWork()
                {
                    lock (self._namesLock)
                    {
                        // update last seen for chatters
                        foreach (var userLogin in userLogins)
                        {
                            // convert to sentence case
                            ref var lastSeen =
                                ref CollectionsMarshal.GetValueRefOrAddDefault
                                    (self._names, userLogin, out _);
                            lastSeen = (null, DateTime.Now);
                        }

                        // remove old chatters
                        var toRemove = new string[Math.Min(self._names.Count, 32)];
                        {
                            rescan:
                            var toRemoveCount = 0;
                            foreach (var chatter in self._names)
                            {
                                var queried = chatter.Value.Queried;
                                if (DateTime.Now - queried > TimeSpan.FromMinutes(30))
                                    toRemove[toRemoveCount++] = chatter.Key;
                                if (toRemoveCount < toRemove.Length)
                                    continue;

                                for (var i = 0; i < toRemoveCount; i++)
                                    self._names.Remove(toRemove[i]);
                                goto rescan;
                            }

                            for (var i = 0; i < toRemoveCount; i++)
                                self._names.Remove(toRemove[i]);
                        }
                    }
                }

                for (;;)
                {
                    SyncWork();

                    if (self._names.Count > cfg.EnoughChatters
                        || pageCount * 100 > total
                        || string.IsNullOrEmpty(cursor))
                        break;

                    (total, cursor, userLogins)
                        = await GetUserLogins(cfg, twitch, broadcaster, moderator, after: cursor);
                    pageCount++;
                }

                // update up to 100 display names at a time
                var logins = new List<string>(100);
                for (;;)
                {
                    lock (self._namesLock)
                    {
                        foreach (var (name, (displayName, queried)) in self._names)
                        {
                            if (displayName is not null) continue;
                            logins.Add(name);
                        }
                    }

                    if (logins.Count == 0) break;

                    var resp = await twitch.Helix.Users.GetUsersAsync(null, logins);

                    lock (self._namesLock)
                    {
                        void UpdateChatter(string login, string displayName)
                        {
                            ref var v = ref CollectionsMarshal.GetValueRefOrNullRef(self._names, login);
                            if (Unsafe.IsNullRef(ref v)) return;
                            foreach (var txf in self._config.DisplayNameTransforms)
                            {
                                var rx = txf.Regex;
                                displayName = rx.Replace(
                                    displayName,
                                    txf.Replace,
                                    txf.MaxMatches // -1 for all
                                );
                            }

                            v = (displayName, v.Queried);
                        }

                        foreach (var user in resp.Users)
                            UpdateChatter(user.Login, user.DisplayName);
                    }

                    logins.Clear();
                }
            }

            if (self._twitchAuthorized)
                AsyncWork().GetAwaiter().GetResult();


            try
            {
                if (self._names.Count == 0)
                    Thread.Sleep(10000);
                else if (self._names.Count >= cfg.EnoughChatters)
                    Thread.Sleep(60000);
                else
                    Thread.Sleep(30000);
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
        } while (!self._lifetime.IsCancellationRequested);
    }

    private static async Task<(int total, string cursor, IEnumerable<string> userLogins)>
        GetUserLogins(OgofConfig config, TwitchAPI twitchAPI, string broadcaster, string moderator,
            string? after = null)
    {
        int total;
        string cursor;
        IEnumerable<string> userLogins;
        {
            var chattersResp = await twitchAPI.Helix.Chat.GetChattersAsync(broadcaster, moderator, after: after);
            total = chattersResp.Total;
            cursor = chattersResp.Pagination.Cursor;
            userLogins = chattersResp.Data.Select(x => x.UserLogin);
        }
        return (total, cursor, userLogins);
    }

    private Rect _twitchAuthPromptRect;

    private GUIStyle _twitchAuthPromptCodeStyle = new()
    {
        alignment = TextAnchor.MiddleCenter,
        fontSize = 48,
        fontStyle = FontStyle.Bold,
        normal =
        {
            textColor = ColorUtility.TryParseHtmlString("#6441A4", out var color) ? color : Color.white
        }
    };

    private readonly string _twitchApiScopes = string.Join(' ', [
        //"channel:read:subscriptions",
        //"moderator:read:followers",
        //"moderator:read:guest_star",
        //"moderator:read:shoutouts",
        //"user:read:chat",
        "moderator:read:chatters"
    ]);

    [ThreadStatic]
    private static Camera? _camCache;

    private unsafe void DrawGui()
    {
        var cam = GetCurrentCamera();

        if (cam is null) return;

        var promptCode = _twitchAuthPromptCode;

        if (promptCode is not null && _config.DrawTwitchAuthCode)
        {
            var fullScreenRect = new Rect(0, 0, Screen.width * 0.25f, Screen.height * 0.25f);
            GUI.Label(fullScreenRect, promptCode, _twitchAuthPromptCodeStyle);
            return;
        }

        int chattersCount;

        lock (_names) chattersCount = _names.Count;

        if (chattersCount <= 0) return;

        var customers = UnityObject.FindObjectsOfType<CustomerGraphics>();

        Span<float> sortBuf = stackalloc float[3];

        foreach (var customer in customers)
        {
            var go = customer.gameObject;
            var md = go.GetComponent<MetadataComponent>();
            int styleIndex;
            string name;
            string? displayName;
            if (md is null)
            {
                md = go.AddComponent<MetadataComponent>();
                var id = customer.gameObject.GetInstanceID();

                lock (_names)
                {
                    var keys = _names.Keys;
                    name = keys.ElementAt(id % keys.Count);
                    displayName = _names.GetValueOrDefault(name).DisplayName;
                }

                styleIndex = Math.Abs(name.GetHashCode()) % _guiStyles.Length;

                md["name"] = name;
                if (displayName is not null)
                    md["displayName"] = displayName;
                md["styleIndex"] = styleIndex;
            }
            else
            {
                name = (string) md["name"]!;
                displayName = (string?) md["displayName"]!;
                styleIndex = (int) md["styleIndex"]!;

                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                if (displayName is null)
                {
                    // check for updated display name
                    lock (_names)
                    {
                        displayName = _names.GetValueOrDefault(name).DisplayName;
                        if (displayName is not null)
                            md["displayName"] = displayName;
                    }
                }
            }

            var style = _guiStyles[styleIndex];

            // get bounds of customer
            var bounds = go.GetComponentInChildren<Renderer>()?.bounds
                         ?? go.GetComponentInChildren<Collider>()?.bounds;

            // find the max of bounds

            Vector2 screenPoint;

            if (bounds is null)
            {
                var pos = go.transform.position
                          + Vector3.up * go.transform.localScale.y;
                screenPoint = cam.WorldToScreenPoint(pos);
            }
            else
            {
                var center = bounds.Value.center;
                var max = bounds.Value.max;
                var min = bounds.Value.min;
                var screenCenter = cam.WorldToScreenPoint(center);
                var screenMax = cam.WorldToScreenPoint(max);
                var screenMin = cam.WorldToScreenPoint(min);

                // pick the highest on vertical axis, center on horizontal
                // this handles rotated cameras

                sortBuf[0] = screenCenter.x;
                sortBuf[1] = screenMax.x;
                sortBuf[2] = screenMin.x;
                sortBuf.Sort();
                ref readonly var centerX = ref sortBuf[1];
                var maxY = Math.Max(Math.Max(screenCenter.y, screenMax.y), screenMin.y);

                screenPoint = new(
                    centerX,
                    maxY);
            }

            var guiDepth = GUI.depth;
            GUI.depth = guiDepth + 1;
            screenPoint.y = Screen.height - screenPoint.y;
            var position = new Rect(screenPoint.x, screenPoint.y, 100, 20);
            DrawName(position, displayName ?? name, style);
            GUI.depth = guiDepth;
        }
    }

    private static Camera? GetCurrentCamera()
    {
        var cam = _camCache;
        if (cam is not null && cam.isActiveAndEnabled)
            return cam;

        cam = Camera.current;
        if (cam is not null && cam.isActiveAndEnabled)
            return cam;

        cam = Camera.main;
        if (cam is not null && cam.isActiveAndEnabled)
            return cam;

        cam = UnityObject.FindObjectsOfType<Camera>()
            .FirstOrDefault(x => x is null || !x.isActiveAndEnabled);
        return cam;
    }

    private static void DrawName(in Rect r, string text, GUIStyle style,
        Color outlineColor = default)
    {
        var cam = GetCurrentCamera();
        if (cam is null)
            return;

        if (outlineColor == default)
            outlineColor = new Color(0, 0, 0, 0.0625f); // 0.5/8

        if (outlineColor.a != 0)
        {
            var s = new GUIStyle
            {
                font = style.font,
                alignment = style.alignment,
                fontSize = style.fontSize,
                fontStyle = style.fontStyle,
                normal = {textColor = outlineColor}
            };
            s.normal.textColor = outlineColor;

            // 0.5 (/2 for avg) * 0.00125 = 0.000625
            var diagOffset = Math.Max(2f, (cam.pixelWidth + cam.pixelHeight) * 0.000625f);
            var offset = new Vector2(diagOffset, diagOffset);

            GUI.Label(new(r.x, r.y - offset.y, r.width, r.height), text, s);
            GUI.Label(new(r.x, r.y + offset.y, r.width, r.height), text, s);
            GUI.Label(new(r.x + offset.x, r.y, r.width, r.height), text, s);
            GUI.Label(new(r.x - offset.x, r.y, r.width, r.height), text, s);

            GUI.Label(new(r.x + offset.x, r.y - offset.y, r.width, r.height), text, s);
            GUI.Label(new(r.x - offset.x, r.y - offset.y, r.width, r.height), text, s);
            GUI.Label(new(r.x + offset.x, r.y + offset.y, r.width, r.height), text, s);
            GUI.Label(new(r.x - offset.x, r.y + offset.y, r.width, r.height), text, s);
        }

        GUI.Label(r, text, style);
    }
}