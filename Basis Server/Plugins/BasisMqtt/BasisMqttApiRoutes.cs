#if !UNITY_2017_1_OR_NEWER
using System;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Basis.Network.Server.Mqtt
{
    /// <summary>
    /// Registers the management commands against <see cref="IServerControl"/>.
    /// Payload field names, validation rules and limits mirror
    /// <see cref="BasisRestApiRoutes"/> so both bindings speak the same dialect;
    /// only the error envelope differs ("ok":false instead of an HTTP status).
    /// </summary>
    public sealed class BasisMqttApiRoutes
    {
        private const int MaxMessageLength = 512;
        private readonly IServerControl _control;

        public BasisMqttApiRoutes(IServerControl control) { _control = control; }

        public void Register(BasisMqttApiHandler handler)
        {
            handler.RegisterCommandHandler("announce", AnnounceAll);
            handler.RegisterCommandHandler("announce-player", AnnouncePlayer);
            handler.RegisterCommandHandler("players/list", ListPlayers);
            handler.RegisterCommandHandler("status", Status);
            handler.RegisterCommandHandler("worlds/list", ListWorlds);
            handler.RegisterCommandHandler("worlds/load", LoadWorld);
            handler.RegisterCommandHandler("worlds/unload", UnloadWorld);
            handler.RegisterCommandHandler("worlds/clear", ClearAllWorlds);
            handler.RegisterCommandHandler("worlds/switch", SwitchWorld);
        }

        private string AnnounceAll(JsonElement body)
        {
            if (!TryGetMessage(body, out var msg, out var error)) return Error(error);
            _control.AnnounceAll(msg);
            return """{"ok":true}""";
        }

        private string AnnouncePlayer(JsonElement body)
        {
            if (!body.TryGetProperty("uuid", out var up) || up.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(up.GetString()))
                return Error("missing uuid");
            if (!TryGetMessage(body, out var msg, out var error)) return Error(error);
            if (!_control.AnnouncePlayer(up.GetString()!, msg)) return Error("player not found");
            return """{"ok":true}""";
        }

        private string ListPlayers(JsonElement _)
        {
            var entries = _control.ListPlayers().Select(p =>
                $$"""{"netId":{{p.NetId}},"uuid":{{JsonSerializer.Serialize(p.Uuid)}},"displayName":{{JsonSerializer.Serialize(p.DisplayName)}},"platform":{{JsonSerializer.Serialize(p.Platform)}},"position":{{JsonSerializer.Serialize(p.Position)}}}""");
            return $$"""{"ok":true,"players":[{{string.Join(",", entries)}}]}""";
        }

        private string Status(JsonElement _)
        {
            int players = _control.ListPlayers().Count;
            int worlds = _control.ListWorlds().Count;
            return $$"""{"ok":true,"online":true,"players":{{players}},"worlds":{{worlds}}}""";
        }

        private string ListWorlds(JsonElement _)
        {
            var entries = _control.ListWorlds().Select(w =>
                $$"""{"netId":{{JsonSerializer.Serialize(w.NetId)}},"url":{{JsonSerializer.Serialize(w.Url)}},"persistent":{{(w.Persistent ? "true" : "false")}},"adminLocked":{{(w.AdminLocked ? "true" : "false")}},"strategy":{{(byte)w.Strategy}}}""");
            return $$"""{"ok":true,"worlds":[{{string.Join(",", entries)}}]}""";
        }

        private string LoadWorld(JsonElement body)
        {
            if (!TryGetUrlAndPassword(body, out var url, out var password, out var error)) return Error(error);

            bool persistent = body.TryGetProperty("persistent", out var pp) && pp.ValueKind == JsonValueKind.True;
            var strategy = LoadStrategy.Immediate;
            if (body.TryGetProperty("strategy", out var sp))
            {
                if (sp.ValueKind == JsonValueKind.String)
                {
                    switch (sp.GetString())
                    {
                        case "synchronized": strategy = LoadStrategy.Synchronized; break;
                        case "predownload":  strategy = LoadStrategy.Predownload;  break;
                        case "immediate":    strategy = LoadStrategy.Immediate;    break;
                        default: return Error("unknown strategy");
                    }
                }
                else if (sp.ValueKind == JsonValueKind.Number && sp.TryGetByte(out byte n))
                {
                    if (!Enum.IsDefined(typeof(LoadStrategy), n)) return Error("unknown strategy");
                    strategy = (LoadStrategy)n;
                }
            }

            var netId = _control.LoadWorld(new WorldLoadParams(url, password, persistent, strategy));
            return $$"""{"ok":true,"netId":{{JsonSerializer.Serialize(netId)}}}""";
        }

        private string UnloadWorld(JsonElement body)
        {
            if (!body.TryGetProperty("netId", out var np) || np.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(np.GetString()))
                return Error("missing netId");
            if (!_control.UnloadWorld(np.GetString()!)) return Error("world not found");
            return """{"ok":true}""";
        }

        private string ClearAllWorlds(JsonElement _)
        {
            int count = _control.ClearAllWorlds();
            return $$"""{"ok":true,"unloaded":{{count}}}""";
        }

        private string SwitchWorld(JsonElement body)
        {
            if (!TryGetUrlAndPassword(body, out var url, out var password, out var error)) return Error(error);

            bool persistent = body.TryGetProperty("persistent", out var pp) && pp.ValueKind == JsonValueKind.True;
            string announce = "";
            if (body.TryGetProperty("announceMessage", out var ap))
            {
                if (ap.ValueKind != JsonValueKind.String) return Error("announceMessage must be a string");
                announce = ap.GetString()!;
                if (announce.Length > MaxMessageLength) return Error($"announceMessage exceeds {MaxMessageLength} characters");
            }

            int delay = 0;
            if (body.TryGetProperty("delay", out var dp))
            {
                if (dp.ValueKind == JsonValueKind.Number && dp.TryGetInt32(out int n) && n >= 0 && n <= 300)
                    delay = n;
                else if (dp.ValueKind != JsonValueKind.Null)
                    return Error("delay must be an integer 0–300 (seconds)");
            }

            var netId = _control.SwitchWorld(new SwitchWorldParams(url, password, persistent, announce, delay));
            return $$"""{"ok":true,"netId":{{JsonSerializer.Serialize(netId)}}}""";
        }

        // ── Parse helpers ──────────────────────────────────────────────────────

        private static bool TryGetMessage(JsonElement body, out string message, out string error)
        {
            message = ""; error = "";
            if (!body.TryGetProperty("message", out var mp)) { error = "missing message"; return false; }
            if (mp.ValueKind != JsonValueKind.String) { error = "message must be a string"; return false; }
            message = mp.GetString()!;
            if (string.IsNullOrEmpty(message)) { error = "message is empty"; return false; }
            if (message.Length > MaxMessageLength) { error = $"message exceeds {MaxMessageLength} characters"; return false; }
            return true;
        }

        private static bool TryGetUrlAndPassword(JsonElement body, out string url, out string password, out string error)
        {
            url = ""; password = ""; error = "";
            if (!body.TryGetProperty("url", out var urlProp)) { error = "missing url"; return false; }
            if (urlProp.ValueKind != JsonValueKind.String) { error = "url must be a string"; return false; }

            var (rawUrl, embedded) = SplitUrlFragment(urlProp.GetString()!);
            string? pw = null;
            if (body.TryGetProperty("password", out var passProp))
            {
                if (passProp.ValueKind != JsonValueKind.String) { error = "password must be a string"; return false; }
                pw = passProp.GetString();
            }
            pw ??= embedded;

            if (string.IsNullOrEmpty(rawUrl)) { error = "url must not be empty"; return false; }
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            { error = "url must use https://"; return false; }
            if (string.IsNullOrEmpty(pw)) { error = "password required (provide password field or embed in url as #fragment)"; return false; }

            url = rawUrl; password = pw;
            return true;
        }

        private static (string url, string? fragment) SplitUrlFragment(string raw)
        {
            raw = raw.Trim();
            int idx = raw.IndexOf('#');
            if (idx >= 0)
                return (raw[..idx], DecodeFragmentPassword(raw[(idx + 1)..]));
            idx = raw.IndexOf("%23", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return (raw[..idx], DecodeFragmentPassword(raw[(idx + 3)..]));
            return (raw, null);
        }

        private static string? DecodeFragmentPassword(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return null;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(fragment)); }
            catch { return fragment; }
        }

        private static string Error(string message) =>
            $$"""{"ok":false,"error":{{JsonSerializer.Serialize(message)}}}""";
    }
}
#endif
