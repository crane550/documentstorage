using System.Net;

namespace DocumentStorage.Web;

public sealed record PublicHosting(Uri? PublicOrigin)
{
    public bool Enabled => PublicOrigin != null;

    public static PublicHosting Read(IConfiguration configuration, bool development)
    {
        var origin = configuration["Hosting:PublicOrigin"];
        if (string.IsNullOrWhiteSpace(origin))
        {
            if (!development) throw new InvalidOperationException("Set Hosting:PublicOrigin to this instance's HTTPS hostname for production hosting.");
            return new(PublicOrigin: null);
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.AbsolutePath != "/" ||
            uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "" || uri.Port != 443 || uri.HostNameType != UriHostNameType.Dns)
            throw new InvalidOperationException("Hosting:PublicOrigin must be an HTTPS origin with no path, query, credentials or custom port.");
        return new(uri);
    }

    public bool AllowsHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)) ||
        (Enabled && host.Equals(PublicOrigin!.Host, StringComparison.OrdinalIgnoreCase));
}
