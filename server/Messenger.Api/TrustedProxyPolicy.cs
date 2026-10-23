using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Messenger.Api;

public static class TrustedProxyPolicy
{
    public static IServiceCollection AddMessengerForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        // Parse now: malformed configuration must fail startup, not the first incoming request.
        var proxies = (configuration.GetSection("Auth:TrustedProxies").Get<string[]>() ?? [])
            .Select(IPAddress.Parse).ToArray();
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Empty KnownProxies AND KnownIPNetworks means trust every peer. No configured proxy means no forwarding.
            options.ForwardedHeaders = proxies.Length == 0 ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
        });
        return services;
    }
}
