using System.Net;
using System.Net.Http.Json;
using Messenger.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Messenger.Tests;

public sealed class TrustedProxyTests
{
    [Theory, Trait("Category", "Unit")]
    [InlineData("", "198.18.0.5", "203.0.113.5", "198.18.0.5")]
    [InlineData("192.0.2.10", "198.18.0.5", "203.0.113.5", "198.18.0.5")]
    [InlineData("192.0.2.10", "192.0.2.10", "203.0.113.5", "203.0.113.5")]
    [InlineData("192.0.2.10", "192.0.2.10", "198.51.100.66, 203.0.113.5", "203.0.113.5")]
    [InlineData("192.0.2.10", "192.0.2.10", "", "192.0.2.10")]
    [InlineData("192.0.2.10", "::ffff:192.0.2.10", "203.0.113.5", "203.0.113.5")]
    public async Task OnlyExplicitTrustedPeerCanForwardOneAddress(string proxy, string peer, string forwarded, string expected)
    {
        var settings = new Dictionary<string, string?>();
        if (proxy.Length != 0) settings["Auth:TrustedProxies:0"] = proxy;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddMessengerForwardedHeaders(configuration);
        await using var app = builder.Build();
        // TestServer has no TCP socket: supply the simulated socket peer BEFORE the real forwarded middleware.
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            return next(context);
        });
        app.UseForwardedHeaders();
        app.Run(context => context.Response.WriteAsJsonAsync(new Address(context.Connection.RemoteIpAddress!.ToString(), context.Request.Scheme)));
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        if (forwarded.Length != 0) request.Headers.Add("X-Forwarded-For", forwarded);
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var address = await response.Content.ReadFromJsonAsync<Address>();
        Assert.Equal(expected, address!.Ip);
        Assert.Equal("http", address.Scheme); // Only X-Forwarded-For is enabled, never other forwarded fields.
    }

    [Theory, Trait("Category", "Unit")]
    [InlineData("proxy.example.com")]
    [InlineData("192.0.2.0/24")]
    public void InvalidProxyConfigurationFailsBeforeServerStartup(string proxy)
    {
        var settings = new Dictionary<string, string?> { ["Auth:TrustedProxies:0"] = proxy };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Throws<FormatException>(() => new ServiceCollection().AddMessengerForwardedHeaders(configuration));
    }

    private sealed record Address(string Ip, string Scheme);
}
