using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ERPSystem.Api.Security;

/// <summary>
/// إصدار/تحقق JWT (HS256) خالي تماماً من الاعتماديات الخارجية.
/// يحل مشكلة System.IO.FileNotFoundException لـ Microsoft.IdentityModel.Tokens.
/// </summary>
public class JwtService
{
    private readonly byte[] _keyBytes;
    private readonly string _issuer;

    public JwtService(string secretHex, string issuer)
    {
        _keyBytes = Convert.FromHexString(secretHex);
        _issuer = issuer;
    }

    public string Issue(string subject, int hours)
    {
        var now = DateTime.UtcNow;
        var expIso = now.AddHours(hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var headerB64 = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"; // {"alg":"HS256","typ":"JWT"}
        var payloadJson = "{\"sub\":\"" + JsonEscape(subject) + "\",\"iss\":\"" + JsonEscape(_issuer) + "\",\"expIso\":\"" + expIso + "\"}";
        var payloadB64 = UrlEncode64(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = headerB64 + "." + payloadB64;

        using var hmac = new HMACSHA256(_keyBytes);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var signatureB64 = UrlEncode64(signature);
        return signingInput + "." + signatureB64;
    }

    public string? VerifySubject(string token)
    {
        try
        {
            if (string.IsNullOrEmpty(token)) return null;
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var signingInput = parts[0] + "." + parts[1];
            using var hmac = new HMACSHA256(_keyBytes);
            var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
            var actual = UrlDecode64(parts[2]);
            if (expected is null || !ConstantTimeEquals(expected, actual)) return null;

            var payloadJson = Encoding.UTF8.GetString(UrlDecode64(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            var payload = doc.RootElement;
            if (!payload.TryGetProperty("iss", out var issNode) || issNode.GetString() != _issuer) return null;

            var subject = payload.TryGetProperty("sub", out var subNode) ? subNode.GetString() : null;
            if (string.IsNullOrEmpty(subject)) return null;

            if (payload.TryGetProperty("expIso", out var expNode))
            {
                var isoNow = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                if (expNode.GetString()?.CompareTo(isoNow) <= 0) return null;
            }
            else return null;

            return subject;
        }
        catch { return null; }
    }

    private static string UrlEncode64(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] UrlDecode64(string input)
    {
        var std = input.Replace('-', '+').Replace('_', '/');
        while (std.Length % 4 != 0) std += "=";
        return Convert.FromBase64String(std);
    }

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string JsonEscape(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }
}
