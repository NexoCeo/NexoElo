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

    public sealed record SolicitarSuporte(string Assunto, string Descricao, string? Canal);

    [HttpPost("chamados")]
    public async Task<IActionResult> AbrirChamado([FromBody] SolicitarSuporte data, CancellationToken cancellationToken)
    {
        if (!User.TryGetUsuarioId(out var usuarioId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(data.Assunto) || string.IsNullOrWhiteSpace(data.Descricao)
            || data.Assunto.Trim().Length > 180 || data.Descricao.Trim().Length > 4000)
            return BadRequest(new { message = "Informe assunto e descrição válidos." });

        var canal=(data.Canal??"chat").Trim().ToLowerInvariant();
        if(canal != "chat" && canal != "email")return BadRequest(new{message="Canal inválido."});
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
            requesterEmail,
            externalUserId = usuarioId.ToString(),
            channel = canal
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
    private async Task<(HttpResponseMessage? Response, IActionResult? Error)> Forward(HttpMethod method,string path,object? payload,CancellationToken ct)
    {
        var apiUrl=_configuration["NexoDesk:ApiUrl"];
        var integrationKey=_configuration["NexoDesk:IntegrationKey"];
        if(string.IsNullOrWhiteSpace(apiUrl)||string.IsNullOrWhiteSpace(integrationKey))
            return (null,StatusCode(503,new{message="Suporte indisponível."}));
        using var request=new HttpRequestMessage(method,apiUrl.TrimEnd('/')+path);
        request.Headers.TryAddWithoutValidation("X-NexoDesk-Integration-Key",integrationKey);
        if(payload is not null)request.Content=JsonContent.Create(payload);
        try{
            var client=_clients.CreateClient();
            client.Timeout=TimeSpan.FromSeconds(12);
            var response=await client.SendAsync(request,ct);
            return (response,null);
        }catch(HttpRequestException){return(null,StatusCode(502,new{message="Falha de comunicação com o suporte."}));}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return(null,StatusCode(504,new{message="Tempo limite do suporte."}));}
    }

    private async Task<IActionResult> Proxy(HttpMethod method,string path,object? payload,CancellationToken ct)
    {
        var (response,error)=await Forward(method,path,payload,ct);
        if(error is not null)return error;
        using(response!){
            if(!response.IsSuccessStatusCode)return StatusCode((int)response.StatusCode,new{message="Não foi possível executar a solicitação."});
            var json=await response.Content.ReadAsStringAsync(ct);
            return Content(json,"application/json");
        }
    }

    [HttpGet("sessao-widget")]
    public async Task<IActionResult> SessaoWidget(CancellationToken ct)
    {
        if(!User.TryGetUsuarioId(out var userId))return Unauthorized();
        var apiUrl=_configuration["NexoDesk:ApiUrl"];
        var key=_configuration["NexoDesk:IntegrationKey"];
        if(string.IsNullOrWhiteSpace(apiUrl)||string.IsNullOrWhiteSpace(key))
            return StatusCode(503,new{message="Integração do widget não configurada."});
        using var request=new HttpRequestMessage(HttpMethod.Post,apiUrl.TrimEnd('/')+"/api/widget/session");
        request.Headers.TryAddWithoutValidation("X-NexoDesk-Integration-Key",key);
        request.Content=JsonContent.Create(new{
            externalUserId=userId.ToString(),
            name=User.FindFirstValue(ClaimTypes.Name)??"Cliente",
            email=User.FindFirstValue(ClaimTypes.Email)??""
        });
        try{
            var client=_clients.CreateClient();client.Timeout=TimeSpan.FromSeconds(12);
            using var response=await client.SendAsync(request,ct);
            if(!response.IsSuccessStatusCode)return StatusCode(502,new{message="Não foi possível obter a sessão do widget."});
            return Content(await response.Content.ReadAsStringAsync(ct),"application/json");
        }catch(HttpRequestException){return StatusCode(502,new{message="NexoDesk indisponível."});}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return StatusCode(504,new{message="Tempo limite do NexoDesk."});}
    }

    [HttpGet("configuracao-widget")]
    public Task<IActionResult> ConfiguracaoWidget(CancellationToken ct)
    {
        if(!User.TryGetUsuarioId(out _))return Task.FromResult<IActionResult>(Unauthorized());
        return Proxy(HttpMethod.Get,"/api/integrations/support/widget-config",null,ct);
    }

    [HttpGet("historico")]
    public Task<IActionResult> Historico(CancellationToken ct)
    {
        if(!User.TryGetUsuarioId(out var id))return Task.FromResult<IActionResult>(Unauthorized());
        var email=User.FindFirstValue(ClaimTypes.Email);
        var uri="/api/integrations/support/history?externalUserId="+Uri.EscapeDataString(id.ToString())+(string.IsNullOrWhiteSpace(email)?"":"&requesterEmail="+Uri.EscapeDataString(email.Trim()));
        return Proxy(HttpMethod.Get,uri,null,ct);
    }

    [HttpGet("conversas/{ticketId:guid}/mensagens")]
    public Task<IActionResult> Mensagens(Guid ticketId,CancellationToken ct)
    {
        if(!User.TryGetUsuarioId(out var id))return Task.FromResult<IActionResult>(Unauthorized());
        return Proxy(HttpMethod.Get,$"/api/integrations/support/{ticketId}/messages?externalUserId={id}",null,ct);
    }

    public sealed record EnviarMensagem(string Mensagem);

    [HttpPost("conversas/{ticketId:guid}/mensagens")]
    public Task<IActionResult> EnviarMensagemChat(Guid ticketId,[FromBody] EnviarMensagem body,CancellationToken ct)
    {
        if(!User.TryGetUsuarioId(out var id))return Task.FromResult<IActionResult>(Unauthorized());
        if(string.IsNullOrWhiteSpace(body.Mensagem)||body.Mensagem.Length>4000)return Task.FromResult<IActionResult>(BadRequest(new{message="Mensagem inválida."}));
        return Proxy(HttpMethod.Post,$"/api/integrations/support/{ticketId}/messages",new{
            externalUserId=id.ToString(),
            senderName=User.FindFirstValue(ClaimTypes.Name)??"Cliente",
            body=body.Mensagem.Trim()
        },ct);
    }

}
