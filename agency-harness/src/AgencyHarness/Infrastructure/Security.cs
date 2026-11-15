using System.Security.Cryptography;
using System.Text;

namespace AgencyHarness.Infrastructure;

public static class Security
{
    /// <summary>API keys are high-entropy random tokens, so a fast SHA-256 is fine
    /// (unlike user passwords, which need a slow salted hash such as PBKDF2/bcrypt).</summary>
    public static string HashApiKey(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    public static string NewApiKey() => "ak_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>HMAC-SHA256 webhook signature over "{timestamp}.{body}". Receivers verify
    /// it and reject stale timestamps to block replay attacks (SD101: webhooks, HTTPS).</summary>
    public static string SignWebhook(string secret, long timestamp, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{body}"));
        return "v1=" + Convert.ToHexString(sig).ToLowerInvariant();
    }

    public static bool VerifyWebhook(string secret, long timestamp, string body, string signature) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(SignWebhook(secret, timestamp, body)),
            Encoding.UTF8.GetBytes(signature));

    public static string Sha256(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
