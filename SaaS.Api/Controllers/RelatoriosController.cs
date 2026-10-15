using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaS.Api.Security;
using SaaS.Api.Services;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Enums;

namespace SaaS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize(Roles = $"{nameof(TipoUsuario.EMPRESA)},{nameof(TipoUsuario.AUTONOMO)}")]
public sealed class RelatoriosController : ControllerBase
{
    private readonly IRelatorioService _relatorioService;
    private readonly PlanoAcessoService? _planos;

    public RelatoriosController(IRelatorioService relatorioService, PlanoAcessoService? planos = null)
    {
        _relatorioService = relatorioService;
        _planos = planos;
    }

    [HttpGet("{tipo}")]
    public async Task<IActionResult> GerarRelatorio(
        string tipo,
        [FromQuery] int ano,
        [FromQuery] int mes)
    {
        if (!User.TryGetUsuarioId(out var usuarioId))
            return Unauthorized();

        if (_planos != null)
        {
            var acesso = await _planos.ConsultarAsync(usuarioId);
            if (!acesso.AcessoAtivo || !acesso.RelatoriosLiberados)
                return StatusCode(403, new { erro = "Relatorios disponiveis a partir do plano Intermediario." });
        }

        if (!TipoRelatorioExtensions.TryFromRoute(tipo, out var tipoRelatorio))
        {
            return BadRequest(new
            {
                erro = "Relatorio invalido. Use resumo-financeiro, servicos-mais-realizados ou agenda-mensal."
            });
        }

        if (ano is < 2000 or > 2100 || mes is < 1 or > 12)
            return BadRequest(new { erro = "Informe um mes e um ano validos." });

        try
        {
            var arquivo = await _relatorioService.GerarRelatorioAsync(
                usuarioId,
                tipoRelatorio,
                ano,
                mes);

            Response.Headers.CacheControl = "no-store";
            return File(arquivo.Conteudo, "application/pdf", arquivo.NomeArquivo);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { erro = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(500, new { erro = "Nao foi possivel gerar o relatorio." });
        }
    }
}
