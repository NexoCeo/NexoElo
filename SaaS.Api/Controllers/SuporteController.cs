using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;
using System.Security.Claims;
using SaaS.Api.Security;

namespace SaaS.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/suporte")]
public sealed class SuporteController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _clients;
    public SuporteController(IConfiguration configuration, IHttpClientFactory clients)
    {
        _configuration = configuration;
        _clients = clients;
    }

    public sealed record SolicitarSuporte(string Assunto, string Descricao);

    [HttpPost("chamados")]
    public async Task<IActionResult> AbrirChamado([FromBody] SolicitarSuporte data, CancellationToken cancellationToken)
    {
        if (!User.TryGetUsuarioId(out _)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(data.Assunto) || string.IsNullOrWhiteSpace(data.Descricao)
            || data.Assunto.Trim().Length > 180 || data.Descricao.Trim().Length > 4000)
            return BadRequest(new { message = "Informe assunto e descrição válidos." });

        var url = _configuration["NexoDesk:ApiUrl"];
        var key = _configuration["NexoDesk:IntegrationKey"];
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            return StatusCode(503, new { message = "Integração de suporte não configurada." });

        var requesterName = User.FindFirstValue(ClaimTypes.Name) ?? "Usuário NexoElo";
        var requesterEmail = User.FindFirstValue(ClaimTypes.Email) ?? "";
        using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/api/integrations/nexoelo/tickets");
        request.Headers.TryAddWithoutValidation("X-NexoDesk-Integration-Key", key);
        request.Content = JsonContent.Create(new
        {
            subject = data.Assunto.Trim(),
            description = data.Descricao.Trim(),
            requesterName,
            requesterEmail
        });
        try
        {
            var client = _clients.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return StatusCode(502, new { message = "O NexoDesk não conseguiu registrar o chamado." });
            var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: cancellationToken);
            return StatusCode(201, result);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(504, new { message = "Tempo limite de comunicação com o NexoDesk." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(502, new { message = "Não foi possível comunicar com o NexoDesk." });
        }
    }
}
