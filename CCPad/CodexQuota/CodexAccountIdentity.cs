using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CCPad.CodexQuota
{
    /// <summary>Local display identity only; never contains credential material.</summary>
    public sealed record CodexAccountIdentity(string Key, string? Email)
    {
        public static CodexAccountIdentity? Read(string home)
        {
            try { return FromJson(File.ReadAllBytes(Path.Combine(home, "auth.json"))); }
            catch { return null; }
        }

        internal static CodexAccountIdentity? FromJson(byte[] bytes)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (ReadString(root, "auth_mode") != "chatgpt" ||
                    (root.TryGetProperty("OPENAI_API_KEY", out var apiKey) && apiKey.ValueKind != JsonValueKind.Null &&
                        (apiKey.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(apiKey.GetString()))) ||
                    !root.TryGetProperty("tokens", out var tokens) ||
                    string.IsNullOrWhiteSpace(ReadString(tokens, "access_token")))
                    return null;
                var account = ReadString(tokens, "account_id");
                if (string.IsNullOrWhiteSpace(account)) return null;

                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)));
                var email = ReadEmail(ReadString(tokens, "id_token")) ??
                    ReadEmail(ReadString(tokens, "access_token"));
                return new CodexAccountIdentity(key, email);
            }
            catch { return null; }
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static string? ReadEmail(string? token)
        {
            // Decode a cached claim for display, never for authentication. Token exp/iat
            // and quota reset times are NOT subscription payment or expiry dates.
            if (string.IsNullOrEmpty(token) || token.Length > 65536) return null;
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return null;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                var root = document.RootElement;
                var email = ReadString(root, "email");
                if (email == null && root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("https://api.openai.com/profile", out var profile))
                    email = ReadString(profile, "email");
                email = email?.Trim();
                return email is { Length: > 3 and <= 254 } && email.Contains('@') &&
                    !email.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? email : null;
            }
            catch { return null; }
        }
    }
}
