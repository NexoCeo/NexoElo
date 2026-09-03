using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using QRCoder;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [EnableRateLimiting("public-read")]
    public class AgendaPublicaController : ControllerBase
    {
        private readonly IUsuarioRepository _usuarioRepository;
        private readonly IServicoRepository _servicoRepository;
        private readonly IVinculoRepository _vinculoRepository;
        private readonly IConfiguration _configuration;

        public AgendaPublicaController(
            IUsuarioRepository usuarioRepository,
            IServicoRepository servicoRepository,
            IVinculoRepository vinculoRepository,
            IConfiguration configuration)
        {
            _usuarioRepository = usuarioRepository;
            _servicoRepository = servicoRepository;
            _vinculoRepository = vinculoRepository;
            _configuration = configuration;
        }

        [HttpGet("prestadores")]
        [AllowAnonymous]
        public async Task<IActionResult> ListarPrestadores()
        {
            var usuarios = await _usuarioRepository.ListarUsuarios();
            var prestadores = usuarios
                .Where(usuario =>
                    usuario.TipoUsuario is TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO &&
                    !string.IsNullOrWhiteSpace(usuario.Slug))
                .Select(usuario => new
                {
                    usuario.Id,
                    usuario.Nome,
                    usuario.TipoUsuario,
                    usuario.Slug,
                    usuario.FotoPerfil
                });

            return Ok(prestadores);
        }

        [HttpGet("empresa/{empresaId:int}/profissionais")]
        [AllowAnonymous]
        public async Task<IActionResult> ListarProfissionaisPublicos(int empresaId)
        {
            if (empresaId <= 0)
                return BadRequest(new { message = "Informe uma empresa valida." });

            var profissionais = await _vinculoRepository.ListarProfissionaisPorEmpresa(empresaId);
            return Ok(profissionais
                .Where(profissional => profissional.VinculoStatus == "APROVADO")
                .Select(profissional => new
                {
                    profissional.Id,
                    profissional.Nome,
                    profissional.FotoPerfil,
                    profissional.VinculoStatus
                }));
        }

        [HttpGet("{slug}")]
        [AllowAnonymous]
        public async Task<ActionResult<AgendaPublicaModel>> ObterAgendaPublica(string slug)
        {
            var usuario = await _usuarioRepository.ListarUsuarioPorSlug(slug);
            if (usuario?.Slug == null)
                return NotFound("Agenda pública não encontrada.");

            var servicos = await _servicoRepository.ListarServicos(usuario.Id);

            return Ok(new AgendaPublicaModel
            {
                UsuarioId = usuario.Id,
                Nome = usuario.Nome,
                TipoUsuario = usuario.TipoUsuario,
                Slug = usuario.Slug,
                FotoPerfil = usuario.FotoPerfil,
                UrlAgendamento = CriarUrlAgendamento(usuario.Slug),
                Servicos = servicos
            });
        }

        [HttpGet("{slug}/qrcode")]
        [AllowAnonymous]
        public async Task<IActionResult> ObterQrCodeAgendaPublica(string slug)
        {
            var usuario = await _usuarioRepository.ListarUsuarioPorSlug(slug);
            if (usuario?.Slug == null)
                return NotFound("Agenda pública não encontrada.");

            var urlAgendamento = CriarUrlAgendamento(usuario.Slug);
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(urlAgendamento, QRCodeGenerator.ECCLevel.Q);
            var qrCode = new SvgQRCode(qrCodeData);
            var svg = qrCode.GetGraphic(8);

            return Content(svg, "image/svg+xml");
        }

        private string CriarUrlAgendamento(string slug)
        {
            var baseUrl = _configuration["App:FrontendBaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = $"{Request.Scheme}://{Request.Host}";

            var path = _configuration["App:AgendamentoPath"];
            if (string.IsNullOrWhiteSpace(path))
                path = "/agendar";

            return $"{baseUrl.TrimEnd('/')}/{path.Trim('/')}/{slug}";
        }
    }
}
