using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaaS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SaaS.Api.Controllers;

/// <summary>
/// Callback publico da Meta. Nunca processa eventos sem assinatura valida.
/// Configure WhatsApp:WebhookVerifyToken e WhatsApp:AppSecret no servidor.
/// </summary>
[ApiController]
[Route("api/WhatsappBusiness/webhook")]
[AllowAnonymous]
public sealed class WhatsappWebhookController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly WhatsAppStore _store;
    public WhatsappWebhookController(IConfiguration configuration, WhatsAppStore store) { _configuration = configuration; _store = store; }

    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        var expected = _configuration["WhatsApp:WebhookVerifyToken"];
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(verifyToken)
            || mode != "subscribe" || !Same(verifyToken, expected)
            || string.IsNullOrEmpty(challenge))
            return Unauthorized();
        return Content(challenge, "text/plain", Encoding.UTF8);
    }

    [HttpPost]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Receive()
    {
        var secret = _configuration["WhatsApp:AppSecret"];
        var signature = Request.Headers["X-Hub-Signature-256"].ToString();
        if (string.IsNullOrEmpty(secret) || !signature.StartsWith("sha256=", StringComparison.Ordinal)
            || signature.Length != 71) return Unauthorized();

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, HttpContext.RequestAborted);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), ms.ToArray());
        byte[] actual;
        try { actual = Convert.FromHexString(signature[7..]); }
        catch (FormatException) { return Unauthorized(); }
        if (actual.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expected)) return Unauthorized();

        // A Meta reenvia eventos ao receber erro; persistimos primeiro e somente depois confirmamos.
        try
        {
            using var payload = JsonDocument.Parse(ms.ToArray());
            if (!payload.RootElement.TryGetProperty("entry", out var entries))
                return BadRequest();
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes)) continue;
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value)) continue;
                    var phone = value.TryGetProperty("metadata", out var metadata)
                        && metadata.TryGetProperty("phone_number_id", out var phoneId) ? phoneId.GetString() : null;
                    if (value.TryGetProperty("statuses", out var statuses))
                        foreach (var item in statuses.EnumerateArray())
                        {
                            var wamid = item.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                            var state = item.TryGetProperty("status", out var st) ? st.GetString() : null;
                            var stamp = item.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;
                            if (string.IsNullOrEmpty(wamid) || string.IsNullOrEmpty(state)) continue;
                            await _store.SaveWebhook($"status:{wamid}:{state}:{stamp}", phone, wamid, "status", state);
                        }
                    if (value.TryGetProperty("messages", out var messages))
                        foreach (var item in messages.EnumerateArray())
                        {
                            var wamid = item.TryGetProperty("id", out var mid) ? mid.GetString() : null;
                            if (!string.IsNullOrEmpty(wamid))
                                await _store.SaveWebhook($"incoming:{wamid}", phone, wamid, "incoming", null);
                        }
                }
            }
            return Ok();
        }
        catch (JsonException) { return BadRequest(); }
        catch (Exception) { return StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static bool Same(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
