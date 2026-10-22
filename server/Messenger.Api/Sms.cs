using System.Collections.Concurrent;
using System.Text.Json;

namespace Messenger.Api;

// Accepted: provider confirmed it took the message. Rejected: definitely not sent, nothing billed.
// Unknown: timeout, transport or unparsable answer. The SMS may still arrive, so it is never resent blindly.
public enum SmsResult { Accepted, Rejected, Unknown }

public interface ISmsSender
{
    Task<SmsResult> Send(string e164, string text, CancellationToken ct);
}

// Deterministic development and test provider. It never performs network I/O.
public sealed class FakeSms : ISmsSender
{
    private readonly ConcurrentQueue<(string Phone, string Text)> sent = new();
    public SmsResult Result { get; set; } = SmsResult.Accepted;
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public IReadOnlyCollection<(string Phone, string Text)> Sent => sent;
    public async Task<SmsResult> Send(string e164, string text, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        sent.Enqueue((e164, text));
        return Result;
    }
}

// SMS.ru HTTP API: POST https://sms.ru/sms/send with api_id, to, msg, json=1.
// The key travels in the form body only, never in the URL. Responses are not logged: they contain the number.
// The contract was taken from secondary sources; re-check it against the official documentation before a paid run.
public sealed class SmsRuSender(HttpClient http, IConfiguration config) : ISmsSender
{
    public const string HttpClientName = "smsru";
    public async Task<SmsResult> Send(string e164, string text, CancellationToken ct)
    {
        var key = config["Auth:Sms:SmsRu:ApiKey"];
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("SMS.ru key is not configured.");
        var form = new Dictionary<string, string> { ["api_id"] = key, ["to"] = e164.TrimStart('+'), ["msg"] = text, ["json"] = "1" };
        if (config["Auth:Sms:SmsRu:From"] is { Length: > 0 } from) form["from"] = from;
        try
        {
            using var response = await http.PostAsync("sms/send", new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode) return SmsResult.Unknown;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Interpret(json.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return SmsResult.Unknown;
        }
    }

    public static SmsResult Interpret(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status)) return SmsResult.Unknown;
        // A top-level ERROR (bad key, wrong method, no money) means the request was not accepted for delivery.
        if (status.ValueEquals("ERROR")) return SmsResult.Rejected;
        if (!status.ValueEquals("OK") || !root.TryGetProperty("sms", out var sms) || sms.ValueKind != JsonValueKind.Object)
            return SmsResult.Unknown;
        var entries = sms.EnumerateObject().ToArray();
        if (entries.Length != 1 || !entries[0].Value.TryGetProperty("status", out var single)) return SmsResult.Unknown;
        return single.ValueEquals("OK") ? SmsResult.Accepted : single.ValueEquals("ERROR") ? SmsResult.Rejected : SmsResult.Unknown;
    }
}
