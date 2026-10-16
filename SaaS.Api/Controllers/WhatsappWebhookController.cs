using System.Security.Cryptography;
using System.Text;
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
    public WhatsappWebhookController(IConfiguration configuration) => _configuration = configuration;

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

        // Nao confirmar processamento de eventos enquanto inbox, idempotencia e status
        // de mensagens ainda nao estiverem implementados.
        return StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static bool Same(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
