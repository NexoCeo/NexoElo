using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaS.Api.Security;
using SaaS.Api.Services;

namespace SaaS.Api.Controllers;

/// <summary>
/// Ponto de entrada protegido para o WhatsApp Embedded Signup.
/// Nao habilita disparos ate que o armazenamento seguro e o webhook estejam disponiveis.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "EMPRESA,AUTONOMO")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WhatsappBusinessController : ControllerBase
{
    private readonly PlanoAcessoService _planos;
    private readonly IConfiguration _config;
    public WhatsappBusinessController(PlanoAcessoService planos, IConfiguration config)
    {
        _planos = planos;
        _config = config;
    }

    private async Task<IActionResult?> ValidarAcesso()
    {
        if (!User.TryGetUsuarioId(out var usuarioId)) return Unauthorized();
        var acesso = await _planos.ConsultarAsync(usuarioId);
        if (!acesso.AcessoAtivo || acesso.Plano != "AVANCADO")
            return StatusCode(StatusCodes.Status403Forbidden, new { erro = "Recurso exclusivo do plano Avancado ativo." });
        return null;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var denial = await ValidarAcesso();
        if (denial != null) return denial;
        return Ok(new {
            conectado = false,
            status = "NAO_CONFIGURADO",
            faturamento = "META_DIRETO",
            confirmacoes = false,
            lembretes = false,
            promocoes = false,
            consumoMes = 0,
            mensagensEnviadas = 0,
            mensagensEntregues = 0,
            mensagensFalhas = 0,
            integracaoDisponivel = false
        });
    }

    [HttpGet("signup-config")]
    public async Task<IActionResult> ConfiguracaoCadastro()
    {
        var denial = await ValidarAcesso();
        if (denial != null) return denial;
        // App ID e Configuration ID sao identificadores publicos, nunca App Secret.
        // Nenhum signup deve ser liberado sem a etapa de persistencia segura implementada.
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
            erro = "O cadastro da Meta ainda nao foi habilitado no servidor.",
            codigo = "WHATSAPP_SETUP_PENDING"
        });
    }

    [HttpPost("conectar")]
    [HttpPut("preferencias")]
    [HttpPost("desconectar")]
    public async Task<IActionResult> OperacaoNaoDisponivel()
    {
        var denial = await ValidarAcesso();
        if (denial != null) return denial;
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new {
            erro = "O armazenamento seguro e o webhook do WhatsApp ainda precisam ser configurados.",
            codigo = "WHATSAPP_SETUP_PENDING"
        });
    }
}
