using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Messenger.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Messenger.Tests;

public sealed class AccessTests
{
    private static IConfiguration Config(byte[] key) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Auth:SigningKey"] = Convert.ToBase64String(key) }).Build();

    [Fact, Trait("Category", "Unit")]
    public void RejectsSigningKeyWithInsufficientEntropy()
    {
        Assert.Throws<InvalidOperationException>(() => Access.Key(Config(new byte[16])));
    }

    [Fact, Trait("Category", "Unit")]
    public void RejectsMalformedActorClaims()
    {
        Assert.Throws<ApiFault>(() => Access.Current(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Guid.NewGuid().ToString()), new Claim("device", "not-a-guid"), new Claim("generation", "1")]))));
    }

    [Fact, Trait("Category", "Unit")]
    public void IssuedTokenBindsDeviceGenerationAndExpiresAfterFifteenMinutes()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var account = new Account { Id = Guid.NewGuid(), Generation = 7 };
        var device = new Device { Id = Guid.NewGuid(), AccountId = account.Id, Generation = 7 };
        var result = new Access(Config(key), clock).Issue(account, device);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(result.AccessToken, new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = Access.Issuer,
            ValidateAudience = true, ValidAudience = Access.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateLifetime = true, ClockSkew = TimeSpan.Zero, ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
        }, out _);
        Assert.Equal(new Actor(account.Id, device.Id, 7), Access.Current(principal));
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), result.ExpiresAt);
        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() => handler.ValidateToken(result.AccessToken,
            new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = Access.Issuer,
                ValidateAudience = true, ValidAudience = Access.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) }, out _));
    }

    private sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
