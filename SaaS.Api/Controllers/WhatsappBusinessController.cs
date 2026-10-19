using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaS.Api.Security;
using SaaS.Api.Services;

namespace SaaS.Api.Controllers;

public sealed record ConectarWhatsApp(string Code,string WabaId,string PhoneNumberId);
public sealed record PreferenciasWhatsApp(bool Confirmacoes,bool Lembretes,bool Promocoes,int HorasAntecedencia,decimal LimiteMensalReais);

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "EMPRESA,AUTONOMO")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WhatsappBusinessController : ControllerBase
{
    private readonly PlanoAcessoService _planos;
    private readonly WhatsAppStore _store;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    public WhatsappBusinessController(PlanoAcessoService planos,WhatsAppStore store,IConfiguration config,IHttpClientFactory http)
    { _planos=planos;_store=store;_config=config;_http=http; }

    private async Task<int?> Owner()
    {
        if(!User.TryGetUsuarioId(out var id)) return null;
        var access=await _planos.ConsultarAsync(id);
        return access.AcessoAtivo && access.Plano=="AVANCADO" ? id : null;
    }
    private IActionResult Denied()=>StatusCode(403,new {erro="Recurso exclusivo do plano Avancado ativo."});

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var id=await Owner(); if(id is null)return Denied();
        if(!Ready())return Ok(new {conectado=false,status="NAO_CONFIGURADO",integracaoDisponivel=false,confirmacoes=false,lembretes=false,promocoes=false,consumoMes=0,mensagensEnviadas=0,mensagensEntregues=0,mensagensFalhas=0});
        return Ok(await _store.Status(id.Value));
    }

    private bool Ready()=>!string.IsNullOrWhiteSpace(_config["WhatsApp:AppId"])
       && !string.IsNullOrWhiteSpace(_config["WhatsApp:ConfigurationId"])
       && !string.IsNullOrWhiteSpace(_config["WhatsApp:AppSecret"])
       && !string.IsNullOrWhiteSpace(_config["WhatsApp:EncryptionKey"])
       && _config.GetValue<bool>("WhatsApp:Enabled");

    [HttpGet("signup-config")]
    public async Task<IActionResult> Signup()
    {
        if(await Owner() is null)return Denied();
        if(!Ready())return StatusCode(503,new {erro="Integracao em configuracao."});
        return Ok(new {appId=_config["WhatsApp:AppId"],configurationId=_config["WhatsApp:ConfigurationId"]});
    }

    [HttpPost("conectar")]
    public async Task<IActionResult> Connect([FromBody] ConectarWhatsApp input)
    {
        var id=await Owner();if(id is null)return Denied();
        if(!Ready())return StatusCode(503,new {erro="Integracao indisponivel."});
        if(string.IsNullOrWhiteSpace(input.Code)||string.IsNullOrWhiteSpace(input.WabaId)||string.IsNullOrWhiteSpace(input.PhoneNumberId))
            return BadRequest(new {erro="Informacoes obrigatorias nao recebidas da Meta."});
        if(input.WabaId.Length>64||input.PhoneNumberId.Length>64||!input.WabaId.All(char.IsAsciiDigit)||!input.PhoneNumberId.All(char.IsAsciiDigit))
            return BadRequest(new {erro="Identificadores Meta invalidos."});

        // O codigo deve ser trocado imediatamente e apenas no backend, nunca persistido.
        var graph=_config["WhatsApp:GraphVersion"] ?? "v23.0";
        var client=_http.CreateClient();
        var tokenUrl=$"https://graph.facebook.com/{graph}/oauth/access_token";
        using var response=await client.PostAsync(tokenUrl,new FormUrlEncodedContent(new Dictionary<string,string>
        {
            ["client_id"]=_config["WhatsApp:AppId"]!,
            ["client_secret"]=_config["WhatsApp:AppSecret"]!,
            ["code"]=input.Code
        }));
        if(!response.IsSuccessStatusCode)return BadRequest(new {erro="A Meta recusou a autorizacao. Tente novamente."});
        using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if(!body.RootElement.TryGetProperty("access_token",out var node))return BadRequest(new {erro="A Meta nao retornou uma credencial valida."});
        var token=node.GetString();
        if(string.IsNullOrWhiteSpace(token))return BadRequest(new {erro="Token invalido."});

        // Verifica que o numero declarado realmente pertence a WABA autorizada.
        using var request=new HttpRequestMessage(HttpMethod.Get,$"https://graph.facebook.com/{graph}/{input.WabaId}/phone_numbers?fields=id,display_phone_number,verified_name&limit=100");
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        using var verified=await client.SendAsync(request);
        if(!verified.IsSuccessStatusCode)return BadRequest(new {erro="Nao foi possivel validar a conta WhatsApp."});
        using var numbers=JsonDocument.Parse(await verified.Content.ReadAsStringAsync());
        string? displayNumber=null,displayName=null;bool found=false;
        if(numbers.RootElement.TryGetProperty("data",out var arr))
            foreach(var item in arr.EnumerateArray())
                if(item.TryGetProperty("id",out var n)&&n.GetString()==input.PhoneNumberId) {
                    found=true;
                    if(item.TryGetProperty("display_phone_number",out var dn))displayNumber=dn.GetString();
                    if(item.TryGetProperty("verified_name",out var vn))displayName=vn.GetString();
                    break;
                }
        if(!found)return BadRequest(new {erro="O numero nao pertence a conta WhatsApp autorizada."});
        try {await _store.SaveConnection(id.Value,input.WabaId,input.PhoneNumberId,token,displayNumber,displayName);}
        catch(Npgsql.PostgresException ex) when(ex.SqlState=="23505"){return Conflict(new {erro="Este numero ja pertence a outro estabelecimento."});}
        return Ok(new {conectado=true});
    }

    [HttpPut("preferencias")]
    public async Task<IActionResult> Preferences([FromBody] PreferenciasWhatsApp pref)
    {
        var id=await Owner();if(id is null)return Denied();
        if(pref.HorasAntecedencia is <1 or >168 || pref.LimiteMensalReais is <0 or >100000)
            return BadRequest(new {erro="Configuracoes fora dos limites permitidos."});
        try {await _store.UpdatePreferences(id.Value,pref.Confirmacoes,pref.Lembretes,pref.Promocoes,pref.HorasAntecedencia,pref.LimiteMensalReais);}
        catch(KeyNotFoundException){return NotFound(new {erro="Conexao nao encontrada."});}
        return NoContent();
    }

    [HttpPost("desconectar")]
    public async Task<IActionResult> Disconnect()
    {
        var id=await Owner();if(id is null)return Denied();
        await _store.Disconnect(id.Value);
        return NoContent();
    }
}
