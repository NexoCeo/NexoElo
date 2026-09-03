using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SaaS.Application.Exceptions;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;

namespace SaaS.Api.Controllers;

[Route("api/[controller]")]
[Route("api/RecuperacaoSenha")]
[ApiController]
[AllowAnonymous]
public class ForgotPasswordController : ControllerBase
{
    private const string RespostaEnvio =
        "Se o e-mail estiver cadastrado, um codigo de recuperacao sera enviado.";

    private readonly IEmailService _emailService;
    private readonly IForgotPasswordRepository _forgotPasswordRepository;
    private readonly IRecuperacaoSenhaRepository _recuperacaoRepository;
    private readonly ILogger<ForgotPasswordController> _logger;
    private readonly byte[] _pepper;

    public ForgotPasswordController(
        IEmailService emailService,
        IForgotPasswordRepository forgotPasswordRepository,
        IRecuperacaoSenhaRepository recuperacaoRepository,
        IConfiguration configuration,
        ILogger<ForgotPasswordController> logger)
    {
        _emailService = emailService;
        _forgotPasswordRepository = forgotPasswordRepository;
        _recuperacaoRepository = recuperacaoRepository;
        _logger = logger;
        var pepper = configuration["PasswordRecovery:Pepper"];
        if (string.IsNullOrWhiteSpace(pepper) || Encoding.UTF8.GetByteCount(pepper) < 32)
            throw new InvalidOperationException(
                "PasswordRecovery:Pepper deve ter pelo menos 32 bytes.");
        _pepper = Encoding.UTF8.GetBytes(pepper);
    }

    [HttpPost("enviar-codigo")]
    [EnableRateLimiting("password-recovery-email")]
    public async Task<IActionResult> EnviarCodigo([FromBody] EnviarCodigoModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Email))
            return Ok(RespostaEnvio);

        var usuario = await _forgotPasswordRepository.ObterUsuarioIdPorEmail(model.Email.Trim());
        if (usuario == null)
            return Ok(RespostaEnvio);

        var codigo = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        await _forgotPasswordRepository.SalvarCodigoRecuperacao(
            usuario.Id,
            CalcularHash(codigo));

        try
        {
            await _emailService.EnviarCodigoAsync(usuario.Email!, codigo, usuario.Nome);
        }
        catch (EmailEnvioException ex)
        {
            _logger.LogError(ex, "Falha ao enviar e-mail de recuperacao para o usuario {UsuarioId}.", usuario.Id);
            await _forgotPasswordRepository.InvalidarCodigosRecuperacao(usuario.Id);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    message = "Nao foi possivel enviar o codigo por e-mail. Tente novamente em instantes.",
                    codigo = ex.Codigo
                });
        }

        return Ok(RespostaEnvio);
    }

    [HttpPost("validar-codigo")]
    [EnableRateLimiting("password-recovery")]
    public async Task<IActionResult> ValidarCodigo([FromBody] ValidarCodigoModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(model.Codigo))
            return BadRequest(new { message = "Codigo invalido ou expirado." });

        var usuario = await _forgotPasswordRepository.ObterUsuarioIdPorEmail(model.Email.Trim());
        if (usuario == null)
            return BadRequest(new { message = "Codigo invalido ou expirado." });

        var recuperacao = await _recuperacaoRepository.ValidarCodigoAsync(
            usuario.Id,
            CalcularHash(model.Codigo.Trim()));
        if (recuperacao == null)
        {
            await _recuperacaoRepository.RegistrarTentativaInvalidaAsync(usuario.Id);
            return BadRequest(new { message = "Codigo invalido ou expirado." });
        }

        var tokenTemporario = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await _recuperacaoRepository.DefinirTokenResetAsync(
            recuperacao.Id,
            CalcularHash(tokenTemporario),
            DateTime.UtcNow.AddMinutes(10));

        return Ok(new { tokenTemporario });
    }

    [HttpPost("redefinir-senha")]
    [EnableRateLimiting("password-recovery")]
    public async Task<IActionResult> RedefinirSenha([FromBody] RedefinirSenhaModel model)
    {
        if (string.IsNullOrWhiteSpace(model.TokenTemporario) ||
            string.IsNullOrWhiteSpace(model.NovaSenha) ||
            model.NovaSenha.Length < 8)
        {
            return BadRequest(new { message = "Token invalido ou senha com menos de 8 caracteres." });
        }

        var redefinida = await _recuperacaoRepository.RedefinirSenhaAsync(
            CalcularHash(model.TokenTemporario.Trim()),
            model.NovaSenha);

        return redefinida
            ? Ok(new { message = "Senha alterada com sucesso." })
            : BadRequest(new { message = "Token invalido ou expirado." });
    }

    private string CalcularHash(string valor)
    {
        using var hmac = new HMACSHA256(_pepper);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(valor)));
    }
}
