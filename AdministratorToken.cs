using System.Text.Json;

namespace Aetheric.Provisioning.Components;

public static class AdministratorToken
{
    // The access token must come from the OIDC middleware's token response, alongside
    // a signature/issuer/audience/nonce-validated ID token. Never accept it from a form.
    public static bool HasAdministratorRole(string? token, string issuer, string clientId, string? subject, string role)
    {
        if (string.IsNullOrWhiteSpace(subject) || token is null || token.Length > 131072) return false;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            var c = json.RootElement;
            return c.GetProperty("iss").GetString() == issuer && c.GetProperty("azp").GetString() == clientId
                && c.GetProperty("sub").GetString() == subject
                && c.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && c.GetProperty("realm_access").GetProperty("roles")
                    .EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == role);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException) { return false; }
    }
}
