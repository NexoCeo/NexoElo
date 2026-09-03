using SaaS.Application.Interfaces.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using SaaS.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using SaaS.Domain.Models;
using SaaS.Domain.Enums;
using SaaS.Application.Interfaces.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace SaaS.Infrastructure.Services
{
    public class AuthService : IAuthService
    {
        private readonly IUsuarioRepository _usuarioRepository;
        private readonly string _chaveSecreta;
        private readonly string _issuer;
        private readonly string _audience;
        private readonly string _connectionString;
        private readonly IPasswordHasher<UsuarioModel> _passwordHasher;

        public AuthService(
            IUsuarioRepository usuarioRepository,
            IConfiguration configuration,
            IPasswordHasher<UsuarioModel> passwordHasher)
        {
            _usuarioRepository = usuarioRepository;
            _passwordHasher = passwordHasher;
            _chaveSecreta = configuration["Jwt:Key"]
                ?? throw new InvalidOperationException("Configuracao Jwt:Key nao encontrada.");
            _issuer = configuration["Jwt:Issuer"]
                ?? throw new InvalidOperationException("Configuracao Jwt:Issuer nao encontrada.");
            _audience = configuration["Jwt:Audience"]
                ?? throw new InvalidOperationException("Configuracao Jwt:Audience nao encontrada.");
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string DefaultConnection nao encontrada.");
        }

        public async Task<UsuarioModel?> Authenticate(string login, string senha)
        {
            var usuario = await _usuarioRepository.ListarUsuarioPorLogin(login);

            if (usuario == null)
            {
                return null;
            }

            var resultado = _passwordHasher.VerifyHashedPassword(usuario, usuario.Senha, senha);
            var senhaLegadaValida = resultado == PasswordVerificationResult.Failed &&
                                    LegacyPasswordHasher.VerificarSenha(senha, usuario.Senha);

            if (resultado == PasswordVerificationResult.Failed && !senhaLegadaValida)
            {
                return null;
            }

            if (senhaLegadaValida || resultado == PasswordVerificationResult.SuccessRehashNeeded)
            {
                var novoHash = _passwordHasher.HashPassword(usuario, senha);
                await _usuarioRepository.AtualizarSenhaHash(usuario.Id, novoHash);
                usuario.Senha = novoHash;
            }

            if (usuario.TipoUsuario == TipoUsuario.PROFISSIONAL)
            {
                usuario.VinculoStatus = await ObterStatusVinculoProfissional(usuario.Id);
            }

            return usuario;
        }

        private async Task<string> ObterStatusVinculoProfissional(int usuarioId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
            SELECT COALESCE(MAX(STATUS_SOLICITACAO), 'NENHUM_VINCULO') AS STATUS_SOLICITACAO
            FROM VINCULOS
            WHERE PROFISSIONAL_FK = @UsuarioId";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@UsuarioId", usuarioId);

            var result = await command.ExecuteScalarAsync();

            return result?.ToString() ?? "NENHUM_VINCULO";
        }

        public async Task<string> GenerateJwtToken(UsuarioModel usuarioModel)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.UTF8.GetBytes(_chaveSecreta);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, usuarioModel.Id.ToString()),
                new Claim(ClaimTypes.Name, usuarioModel.Nome),
                new Claim(ClaimTypes.Role, usuarioModel.TipoUsuario.ToString())
            };

            if (!string.IsNullOrWhiteSpace(usuarioModel.Email))
                claims.Add(new Claim(ClaimTypes.Email, usuarioModel.Email));

            if (!string.IsNullOrWhiteSpace(usuarioModel.Telefone))
                claims.Add(new Claim(ClaimTypes.MobilePhone, usuarioModel.Telefone));

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Issuer = _issuer,
                Audience = _audience,
                Expires = DateTime.UtcNow.AddHours(2),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(key),
                    SecurityAlgorithms.HmacSha256Signature
                )
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);

        }
    }
}


