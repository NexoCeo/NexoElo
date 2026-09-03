using SaaS.Domain.Models;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SaaS.Api.Security;
using SaaS.Domain.Enums;
using System.Text;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ServicoController : ControllerBase
    {
        private readonly IServicoRepository _servicoRepository;
        private readonly IArquivoUploadService? _arquivoUploadService;

        public ServicoController(
            IServicoRepository servicoRepository,
            IArquivoUploadService? arquivoUploadService = null)
        {
            _servicoRepository = servicoRepository;
            _arquivoUploadService = arquivoUploadService;
        }

        [HttpPost("InserirServico")]
        [HttpPost("InserirServiço")]
        [Consumes("application/json")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> InserirServico([FromBody] ServicoModel servico)
        {
            if (!User.TryGetUsuarioId(out var usuarioId))
                return Forbid();

            servico.UsuarioFk = usuarioId;
            servico.EmpresaId = User.IsInRole(nameof(TipoUsuario.EMPRESA)) ? usuarioId : null;
            servico.ProfissionalId = User.IsInRole(nameof(TipoUsuario.AUTONOMO)) ? usuarioId : null;

            try
            {
                var result = await _servicoRepository.InserirServico(servico);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost("InserirServicoComImagem")]
        [HttpPost("InserirServiçoComImagem")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> InserirServicoComImagem([FromForm] CreateServicoModel request)
        {
            if (!User.TryGetUsuarioId(out var usuarioId))
                return Forbid();

            string? caminhoImagem = null;

            if (request.ImagemServico is { Length: > 0 })
            {
                const long tamanhoMaximo = 5 * 1024 * 1024;
                var extensao = Path.GetExtension(request.ImagemServico.FileName).ToLowerInvariant();
                var extensoesPermitidas = new HashSet<string> { ".jpg", ".jpeg", ".png", ".webp" };
                var tiposPermitidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "image/jpeg", "image/png", "image/webp"
                };

                if (request.ImagemServico.Length > tamanhoMaximo ||
                    !extensoesPermitidas.Contains(extensao) ||
                    !tiposPermitidos.Contains(request.ImagemServico.ContentType) ||
                    !await ArquivoImagemValido(request.ImagemServico, extensao))
                {
                    return BadRequest("A imagem do servico deve ser JPG, PNG ou WEBP e ter no maximo 5 MB.");
                }

                caminhoImagem = await SalvarImagemServico(request.ImagemServico);
            }

            var servico = new ServicoModel
            {
                UsuarioFk = usuarioId,
                EmpresaId = User.IsInRole(nameof(TipoUsuario.EMPRESA)) ? usuarioId : null,
                ProfissionalId = User.IsInRole(nameof(TipoUsuario.AUTONOMO)) ? usuarioId : null,
                NomeServico = request.NomeServico,
                Valor = request.Valor,
                TempoEstimadoMinutos = request.TempoEstimadoMinutos,
                ImagemServico = caminhoImagem
            };

            try
            {
                var result = await _servicoRepository.InserirServico(servico);
                return Ok(result);
            }
            catch (ArgumentException ex)
            {
                await RemoverImagem(caminhoImagem);
                return BadRequest(ex.Message);
            }
            catch
            {
                await RemoverImagem(caminhoImagem);
                throw;
            }
        }

        [HttpPut("{id:int}")]
        [HttpPatch("{id:int}")]
        [Consumes("multipart/form-data")]
        [Authorize(Roles = "EMPRESA,AUTONOMO")]
        public async Task<IActionResult> AtualizarServico(int id, [FromForm] UpdateServicoModel request)
        {
            if (!User.TryGetUsuarioId(out var usuarioId))
                return Forbid();

            if (id <= 0 || request == null)
                return BadRequest("Informe um servico valido para atualizar.");

            string? caminhoImagem = null;

            try
            {
                var servicosDoUsuario = await _servicoRepository.ListarServicos(usuarioId);
                var servicoAtual = servicosDoUsuario.FirstOrDefault(servico => servico.Id == id);
                if (servicoAtual == null)
                    return NotFound("Servico nao encontrado.");

                caminhoImagem = await SalvarImagemServico(request.ImagemServico);
                var atualizarImagem = caminhoImagem != null;
                var servico = new ServicoModel
                {
                    UsuarioFk = usuarioId,
                    EmpresaId = User.IsInRole(nameof(TipoUsuario.EMPRESA)) ? usuarioId : null,
                    ProfissionalId = User.IsInRole(nameof(TipoUsuario.AUTONOMO)) ? usuarioId : null,
                    NomeServico = request.NomeServico,
                    Valor = request.Valor,
                    TempoEstimadoMinutos = request.TempoEstimadoMinutos,
                    ImagemServico = caminhoImagem
                };

                var atualizado = await _servicoRepository.AtualizarServico(
                    id,
                    usuarioId,
                    servico,
                    atualizarImagem);

                if (atualizado == null)
                {
                    await RemoverImagem(caminhoImagem);
                    return NotFound("Servico nao encontrado.");
                }

                if (atualizarImagem)
                    await RemoverImagem(servicoAtual.ImagemServico);

                return Ok(atualizado);
            }
            catch (ArgumentException ex)
            {
                await RemoverImagem(caminhoImagem);
                return BadRequest(ex.Message);
            }
            catch
            {
                await RemoverImagem(caminhoImagem);
                throw;
            }
        }

        [HttpGet("ListarServicosPorEmpresa")]
        [HttpGet("ListarServiçosPorEmpresa")]
        [EnableRateLimiting("public-read")]
        public async Task<ActionResult<List<ServicoModel>>> ListarServicos(int id)
        {
            try
            {
                var servicos = await _servicoRepository.ListarServicos(id);

                return Ok(servicos);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        private async Task RemoverImagem(string? caminhoImagem)
        {
            if (string.IsNullOrWhiteSpace(caminhoImagem))
                return;

            if (_arquivoUploadService != null)
                await _arquivoUploadService.RemoverAsync(caminhoImagem);

            var caminhoCompleto = Path.Combine(
                Directory.GetCurrentDirectory(),
                "wwwroot",
                caminhoImagem.Replace('/', Path.DirectorySeparatorChar));

            if (System.IO.File.Exists(caminhoCompleto))
                System.IO.File.Delete(caminhoCompleto);
        }

        private async Task<string?> SalvarImagemServico(IFormFile? imagemServico)
        {
            if (imagemServico is not { Length: > 0 })
                return null;

            const long tamanhoMaximo = 5 * 1024 * 1024;
            var extensao = Path.GetExtension(imagemServico.FileName).ToLowerInvariant();
            var extensoesPermitidas = new HashSet<string> { ".jpg", ".jpeg", ".png", ".webp" };
            var tiposPermitidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg", "image/png", "image/webp"
            };

            if (imagemServico.Length > tamanhoMaximo ||
                !extensoesPermitidas.Contains(extensao) ||
                !tiposPermitidos.Contains(imagemServico.ContentType) ||
                !await ArquivoImagemValido(imagemServico, extensao))
            {
                throw new ArgumentException(
                    "A imagem do servico deve ser JPG, PNG ou WEBP e ter no maximo 5 MB.");
            }

            if (_arquivoUploadService == null)
                throw new InvalidOperationException("O armazenamento de imagens nao foi configurado.");

            await using var stream = imagemServico.OpenReadStream();
            using var memoria = new MemoryStream();
            await stream.CopyToAsync(memoria, HttpContext.RequestAborted);

            return await _arquivoUploadService.SalvarAsync(
                memoria.ToArray(),
                extensao,
                imagemServico.ContentType,
                HttpContext.RequestAborted);
        }

        private static async Task<bool> ArquivoImagemValido(IFormFile arquivo, string extensao)
        {
            var cabecalho = new byte[12];
            await using var stream = arquivo.OpenReadStream();
            var bytesLidos = await stream.ReadAsync(cabecalho);

            return extensao switch
            {
                ".jpg" or ".jpeg" =>
                    bytesLidos >= 3 && cabecalho[0] == 0xFF && cabecalho[1] == 0xD8 && cabecalho[2] == 0xFF,
                ".png" =>
                    bytesLidos >= 8 && cabecalho[..8].SequenceEqual(
                        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                ".webp" =>
                    bytesLidos >= 12 &&
                    Encoding.ASCII.GetString(cabecalho, 0, 4) == "RIFF" &&
                    Encoding.ASCII.GetString(cabecalho, 8, 4) == "WEBP",
                _ => false
            };
        }
    }
}

