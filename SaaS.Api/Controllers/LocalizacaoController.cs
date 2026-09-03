using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Application.Exceptions;
using SaaS.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authorization;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous]
    [EnableRateLimiting("public-read")]
    public class LocalizacaoController : ControllerBase
    {
        private readonly ILocalizacaoRepository _localizacaoRepository;
        private readonly ILocalizacaoCadastroService _localizacaoCadastroService;

        public LocalizacaoController(
            ILocalizacaoRepository localizacaoRepository,
            ILocalizacaoCadastroService localizacaoCadastroService)
        {
            _localizacaoRepository = localizacaoRepository;
            _localizacaoCadastroService = localizacaoCadastroService;
        }

        [HttpGet("paises")]
        public async Task<IActionResult> ListarPaises()
        {
            var paises = await _localizacaoRepository.ListarPaises();
            return Ok(paises);
        }

        [HttpGet("paises/{paisId:int}/estados")]
        public async Task<IActionResult> ListarEstadosPorPais(int paisId)
        {
            if (paisId <= 0)
                return BadRequest("Pais invalido.");

            var estados = await _localizacaoRepository.ListarEstadosPorPais(paisId);
            return Ok(estados);
        }

        [HttpGet("estados/{estadoId:int}/cidades")]
        public async Task<IActionResult> ListarCidadesPorEstado(int estadoId)
        {
            if (estadoId <= 0)
                return BadRequest("Estado invalido.");

            var cidades = await _localizacaoRepository.ListarCidadesPorEstado(estadoId);
            return Ok(cidades);
        }

        [HttpPost("resolver")]
        public async Task<IActionResult> ResolverCoordenadas([FromBody] CoordenadasModel coordenadas)
        {
            if (coordenadas == null)
                return BadRequest("Informe latitude e longitude.");

            try
            {
                var localizacao = await _localizacaoCadastroService.ResolverAutomaticamenteAsync(
                    coordenadas,
                    HttpContext.RequestAborted);
                return Ok(localizacao);
            }
            catch (ArgumentException ex)
            {
                return UnprocessableEntity(new
                {
                    code = "LOCALIZACAO_NAO_RESOLVIDA",
                    message = ex.Message
                });
            }
            catch (GeocodingIndisponivelException)
            {
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new { message = "O servico de localizacao esta indisponivel. Tente novamente." });
            }
        }
    }
}
