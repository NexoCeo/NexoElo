using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Api.Security;
using SaaS.Domain.Models;

namespace SaaS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IVinculoRepository _vinculoRepository;

        public AuthController(IAuthService authService, IVinculoRepository vinculoRepository)
        {
            _authService = authService;
            _vinculoRepository = vinculoRepository;
        }
        
        [HttpPost("AutenticarUsuario")]
        [AllowAnonymous]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> Login([FromBody] AuthUsuarioModel authModel)
        {
            if (authModel == null)
                return BadRequest(new { message = "Dados de login inválidos" });

            var login = ObterPrimeiroValorPreenchido(authModel.Login, authModel.Email, authModel.Telefone);
            if (string.IsNullOrWhiteSpace(login))
                return BadRequest(new { message = "Informe email ou telefone" });

            if (string.IsNullOrWhiteSpace(authModel.Senha))
                return BadRequest(new { message = "Informe a senha" });

            var usuario = await _authService.Authenticate(login, authModel.Senha);

            if (usuario == null)
                return Unauthorized(new { message = "Email ou senha inválidos" });

            var token = await _authService.GenerateJwtToken(usuario);
            Response.Cookies.Append(AuthCookie.Name, token, AuthCookie.CreateOptions(Request));

            return Ok(new
            {
                usuario = new
                {
                    usuario.Id,
                    usuario.Nome,
                    usuario.Email,
                    usuario.Telefone,
                    usuario.Slug,
                    usuario.FotoPerfil,
                    usuario.TipoUsuario,
                    usuario.AssinaturaAtiva,
                    VinculoStatus = usuario.VinculoStatus
                },
                message = "Login realizado com sucesso"
            });
        }

        [HttpPost("MigrarSessao")]
        [Authorize]
        public IActionResult MigrarSessao()
        {
            var authorization = Request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";

            if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                return Unauthorized();

            var token = authorization[bearerPrefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(token))
                return Unauthorized();

            Response.Cookies.Append(AuthCookie.Name, token, AuthCookie.CreateOptions(Request));
            return NoContent();
        }

        [HttpPost("Logout")]
        [AllowAnonymous]
        public IActionResult Logout()
        {
            Response.Cookies.Delete(AuthCookie.Name, AuthCookie.CreateOptions(Request));
            return NoContent();
        }

        private static string? ObterPrimeiroValorPreenchido(params string?[] valores)
        {
            foreach (var valor in valores)
            {
                if (!string.IsNullOrWhiteSpace(valor))
                    return valor.Trim();
            }

            return null;
        }
    }
}

