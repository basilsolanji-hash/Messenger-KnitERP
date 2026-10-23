using System.Collections.Concurrent;
using System.Text.Json;

namespace Messenger.Api;

// Accepted: provider confirmed acceptance for this recipient. Rejected: explicit provider refusal.
// Neither result proves delivery or the amount billed; the durable budget reservation is retained.
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

// Contract: https://sms.ru/docs/api/api_group_sms/send (json=1).
// The key travels in the form body only. Provider responses contain phone numbers and are never logged.
public sealed class SmsRuSender(HttpClient http, IConfiguration config) : ISmsSender
{
    public const string HttpClientName = "smsru";
    public const int MaxResponseBytes = 16 * 1024;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    private static readonly Uri Endpoint = new("https://sms.ru/sms/send");

    // A 307/308 must never forward the credential-bearing form to another endpoint.
    public static SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0
    };

    public async Task<SmsResult> Send(string e164, string text, CancellationToken ct)
    {
        if (e164.Length is < 3 or > 16 || e164[0] != '+' || e164[1] is < '1' or > '9' ||
            e164.AsSpan(2).ContainsAnyExceptInRange('0', '9'))
            throw new ArgumentException("A single canonical E.164 number is required.", nameof(e164));
        var key = config["Auth:Sms:SmsRu:ApiKey"];
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("SMS.ru key is not configured.");
        var recipient = e164[1..];
        var form = new Dictionary<string, string> { ["api_id"] = key, ["to"] = recipient, ["msg"] = text, ["json"] = "1" };
        if (config["Auth:Sms:SmsRu:From"] is { Length: > 0 } from) form["from"] = from;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CallTimeout); // Covers the body too, unlike HttpClient's headers-only timeout.
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(form) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxResponseBytes)
                return SmsResult.Unknown;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[MaxResponseBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxResponseBytes) return SmsResult.Unknown;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            return Interpret(json.RootElement, recipient);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            // No automatic retry: even a timeout or malformed response can follow a paid send.
            return SmsResult.Unknown;
        }
    }

    public static SmsResult Interpret(JsonElement root, string recipient)
    {
        if (!UniqueObject(root) || !Status(root, out var status, out var code)) return SmsResult.Unknown;
        if (status == "ERROR")
            return Refusal(code) && !root.TryGetProperty("sms", out _) ? SmsResult.Rejected : SmsResult.Unknown;
        if (status != "OK" || code != 100 || !root.TryGetProperty("sms", out var sms) || !UniqueObject(sms))
            return SmsResult.Unknown;
        var entries = sms.EnumerateObject().ToArray();
        if (entries.Length != 1 || entries[0].Name != recipient || !UniqueObject(entries[0].Value) ||
            !Status(entries[0].Value, out status, out code)) return SmsResult.Unknown;
        var single = entries[0].Value;
        if (status == "ERROR")
            return Refusal(code) && !single.TryGetProperty("sms_id", out _) ? SmsResult.Rejected : SmsResult.Unknown;
        if (status != "OK" || code != 100 || !single.TryGetProperty("sms_id", out var id) ||
            id.ValueKind != JsonValueKind.String) return SmsResult.Unknown;
        var value = id.GetString()!;
        return value.Length is > 0 and <= 128 && value.All(c => c is >= '!' and <= '~')
            ? SmsResult.Accepted : SmsResult.Unknown;
    }

    private static bool UniqueObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(p => names.Add(p.Name));
    }

    private static bool Status(JsonElement value, out string? status, out int code)
    {
        status = null;
        code = 0;
        if (!value.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.String ||
            !value.TryGetProperty("status_code", out var c) || c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out code))
            return false;
        status = s.GetString();
        return true;
    }

    // Documented send refusals only; delivery statuses and server error 500 remain ambiguous.
    private static bool Refusal(int code) => code is >= 200 and <= 217 or >= 220 and <= 222 or
        >= 230 and <= 233 or >= 300 and <= 305 or >= 501 and <= 509 or 550;
}
