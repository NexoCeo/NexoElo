using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SaaS.Api.Security;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Models;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FuncionamentoController : ControllerBase
    {
        private readonly IFuncionamentoRepository _funcionamentoRepository;

        public FuncionamentoController(IFuncionamentoRepository funcionamentoRepository)
        {
            _funcionamentoRepository = funcionamentoRepository;
        }

        [HttpGet("{usuarioId:int}")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> ObterFuncionamento(int usuarioId)
        {
            if (!User.EhProprioUsuario(usuarioId))
                return Forbid();

            try
            {
                var funcionamento = await _funcionamentoRepository.ObterFuncionamento(usuarioId);
                if (funcionamento == null)
                    return NotFound("Configuracao de funcionamento nao encontrada.");

                return Ok(funcionamento);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Nao foi possivel obter o funcionamento." });
            }
        }

        [HttpPut("{usuarioId:int}")]
        [HttpPatch("{usuarioId:int}")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> SalvarFuncionamento(int usuarioId, [FromBody] FuncionamentoConfigModel funcionamento)
        {
            if (!User.EhProprioUsuario(usuarioId))
                return Forbid();

            if (funcionamento == null)
                return BadRequest(new { message = "Dados de funcionamento invalidos." });

            try
            {
                funcionamento.UsuarioFk = usuarioId;
                var configuracaoSalva = await _funcionamentoRepository.SalvarFuncionamento(funcionamento);
                return Ok(configuracaoSalva);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception)
            {
                return StatusCode(500, new { message = "Nao foi possivel salvar o funcionamento." });
            }
        }
    }
}
