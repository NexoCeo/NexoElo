using SaaS.Domain.Models;
using SaaS.Application.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaS.Api.Security;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ProfissionalController : ControllerBase
    {
        private readonly IProfissionalRepository _profissionalRepository;
        private readonly IVinculoRepository _vinculoRepository;
        public ProfissionalController(IProfissionalRepository profissionalRepository, IVinculoRepository vinculoRepository)
        {
            _profissionalRepository = profissionalRepository;
            _vinculoRepository = vinculoRepository;
        }

        [HttpGet("empresas-na-cidade/{profissionalId}")]
        [Authorize(Roles = "PROFISSIONAL")]
        public async Task<IActionResult> ListarEmpresasMesmaCidade(int profissionalId)
        {
            if (!User.EhProprioUsuario(profissionalId))
                return Forbid();

            var empresas = await _profissionalRepository.ListarEmpresasMesmaCidade(profissionalId);

            if (empresas == null || !empresas.Any())
                return NotFound("Nenhuma empresa encontrada.");

            return Ok(empresas);
        }

        [HttpGet("empresas")]
        [AllowAnonymous]
        public async Task<IActionResult> ListarEmpresasPorCidade(
            [FromQuery] int? cidadeId)
        {
            // Sem cidade: pré-carregamento público de id/nome das empresas,
            // independente de GPS ou papel selecionado.
            if (!cidadeId.HasValue)
            {
                var todasEmpresas = await _profissionalRepository
                    .ListarEmpresasDisponiveisParaCadastro();
                return Ok(todasEmpresas);
            }

            // Mantém compatibilidade com consultas filtradas por cidade.
            if (cidadeId.Value <= 0)
                return BadRequest("Cidade inválida.");

            var empresas = await _profissionalRepository
                .ListarEmpresasPorCidade(cidadeId.Value);

            return Ok(empresas);
        }

        [HttpPost("solicitar-vinculo")]
        [Authorize(Roles = "PROFISSIONAL")]
        public async Task<IActionResult> SolicitarVinculo([FromBody] SolicitarVinculoModel model)
        {
            if (!User.EhProprioUsuario(model.ProfissionalId))
                return Forbid();

            if (await _vinculoRepository.JaExisteSolicitacaoPendente(model.ProfissionalId, model.EmpresaId))
                return BadRequest("Já existe uma solicitação pendente.");

            await _vinculoRepository.CriarSolicitacaoVinculo(model.ProfissionalId, model.EmpresaId);

            return Ok("Solicitação enviada com sucesso.");
        }

    }
}

