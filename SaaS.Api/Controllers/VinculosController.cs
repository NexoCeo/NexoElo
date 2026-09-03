using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Models;
using SaaS.Api.Security;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class VinculosController : ControllerBase
    {
        private readonly IVinculoRepository _vinculoRepository;

        public VinculosController(IVinculoRepository vinculoRepository)
        {
            _vinculoRepository = vinculoRepository;
        }

        [HttpGet("profissionais/empresa/{empresaId:int}")]
        [Authorize(Roles = "EMPRESA")]
        public async Task<IActionResult> ListarProfissionaisPorEmpresa(int empresaId)
        {
            if (empresaId <= 0)
                return BadRequest(new { message = "Informe uma empresa valida." });

            if (!User.EhProprioUsuario(empresaId))
                return Forbid();

            try
            {
                var profissionais = await _vinculoRepository.ListarProfissionaisPorEmpresa(empresaId);
                return Ok(profissionais);
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Nao foi possivel listar os profissionais." });
            }
        }

        [HttpGet("empresa/{empresaId:int}/solicitacoes")]
        [Authorize(Roles = "EMPRESA")]
        public async Task<IActionResult> ListarSolicitacoesPendentes(int empresaId)
        {
            if (!User.EhProprioUsuario(empresaId))
                return Forbid();

            var solicitacoes = await _vinculoRepository.ListarSolicitacoesPendentesPorEmpresa(empresaId);
            return Ok(solicitacoes);
        }

        [HttpPatch("empresa/{empresaId:int}/solicitacoes/{solicitacaoId:int}")]
        [Authorize(Roles = "EMPRESA")]
        public async Task<IActionResult> ResponderSolicitacao(
            int empresaId,
            int solicitacaoId,
            [FromBody] RespostaSolicitacaoModel request)
        {
            if (!User.EhProprioUsuario(empresaId))
                return Forbid();

            var status = request?.Status?.Trim().ToUpperInvariant();
            if (status is not ("APROVADO" or "RECUSADO"))
                return BadRequest(new { message = "Informe APROVADO ou RECUSADO." });

            var atualizado = await _vinculoRepository.ResponderSolicitacaoAsync(
                solicitacaoId,
                empresaId,
                status);

            return atualizado
                ? NoContent()
                : Conflict(new { message = "A solicitacao nao esta mais pendente ou nao pertence a empresa." });
        }

        [HttpGet("profissional/status")]
        [Authorize(Roles = "PROFISSIONAL")]
        public async Task<IActionResult> ObterStatusProfissional()
        {
            if (!User.TryGetUsuarioId(out var profissionalId))
                return Forbid();

            var vinculo = await _vinculoRepository.ObterVinculoAtualDoProfissionalAsync(profissionalId);
            return vinculo == null
                ? Ok(new { VinculoStatus = "SEM_VINCULO", EmpresaId = (int?)null, EmpresaNome = (string?)null })
                : Ok(new
                {
                    vinculo.VinculoStatus,
                    vinculo.EmpresaId,
                    vinculo.EmpresaNome,
                    vinculo.DataSolicitacao
                });
        }

        [HttpGet("profissionais/{profissionalId:int}/empresa/{empresaId:int}/servicos")]
        [AllowAnonymous]
        [EnableRateLimiting("public-read")]
        public async Task<IActionResult> ListarServicosDoProfissional(int profissionalId, int empresaId)
        {
            try
            {
                var servicos = await _vinculoRepository.ListarServicosDoProfissional(profissionalId, empresaId);
                return Ok(servicos);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Nao foi possivel listar os servicos do profissional." });
            }
        }

        [HttpPut("profissionais/{profissionalId:int}/empresa/{empresaId:int}/servicos")]
        [HttpPatch("profissionais/{profissionalId:int}/empresa/{empresaId:int}/servicos")]
        [Authorize(Roles = "EMPRESA")]
        public async Task<IActionResult> VincularServicosProfissional(
            int profissionalId,
            int empresaId,
            [FromBody] VincularServicosProfissionalModel request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados do vinculo de servicos invalidos." });

            if (!User.EhProprioUsuario(empresaId))
                return Forbid();

            try
            {
                var servicos = await _vinculoRepository.VincularServicosProfissional(
                    profissionalId,
                    empresaId,
                    request.ServicoIds);

                return Ok(servicos);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Nao foi possivel vincular os servicos ao profissional." });
            }
        }
    }
}