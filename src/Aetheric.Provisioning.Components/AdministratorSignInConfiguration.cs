using System.Net;

namespace Aetheric.Provisioning.Components;

public sealed class AdministratorSignInConfiguration
{
    public Uri? Origin { get; }
    public bool Enabled { get; }
    public Uri Callback => new(Origin!, "/setup/signin-oidc");
    public bool IsLocalHttp => Origin?.Scheme == "http";

    public AdministratorSignInConfiguration(BootstrapConnectionConfiguration connection, bool development)
    {
        var value = connection.PublicOrigin;
        if (string.IsNullOrWhiteSpace(value) && development) value = "http://127.0.0.1:5180";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == "/"
            && (uri.Scheme == "https" || development && uri.Scheme == "http"
                && (uri.Host == "localhost" || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address))))
            Origin = uri;
        Enabled = connection.IsConfigured && Origin is not null;
    }

    public bool Matches(HttpRequest request) => Origin is not null
        && string.Equals(request.Scheme + "://" + request.Host, Origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
