using Microsoft.Extensions.Configuration;
using SaaS.Domain.Models;
using SaaS.Application.Interfaces.Repositories;
using Npgsql;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class ForgotPasswordRepository : IForgotPasswordRepository
    {
        private readonly string _connectionString;

        public ForgotPasswordRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string DefaultConnection nao encontrada.");
        }

        public async Task SalvarCodigoRecuperacao(int usuarioId, string codigoHash)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var invalidar = new NpgsqlCommand(@"
                UPDATE RECUPERACAO_SENHA
                SET USADO = TRUE
                WHERE USUARIO_FK = @UsuarioId
                  AND USADO = FALSE", connection, transaction))
            {
                invalidar.Parameters.AddWithValue("@UsuarioId", usuarioId);
                await invalidar.ExecuteNonQueryAsync();
            }

            var command = new NpgsqlCommand(string.Empty, connection, transaction);
            command.CommandText = @"
            INSERT INTO RECUPERACAO_SENHA (
                USUARIO_FK,
                CODIGO,
                CODIGO_HASH,
                DATA_EXPIRACAO,
                TENTATIVAS,
                CODIGO_VALIDADO,
                USADO)
            VALUES (@UsuarioId, 'PROTEGIDO', @CodigoHash, @DataExpiracao, 0, FALSE, FALSE)";

            command.Parameters.AddWithValue("UsuarioId", usuarioId);
            command.Parameters.AddWithValue("CodigoHash", codigoHash);
            command.Parameters.AddWithValue("DataExpiracao", DateTime.UtcNow.AddMinutes(10));

            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        public async Task InvalidarCodigosRecuperacao(int usuarioId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(@"
                UPDATE RECUPERACAO_SENHA
                SET USADO = TRUE
                WHERE USUARIO_FK = @UsuarioId
                  AND USADO = FALSE", connection);
            command.Parameters.AddWithValue("UsuarioId", usuarioId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<UsuarioEmail?> ObterUsuarioIdPorEmail(string email)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT ID_USUARIO, EMAIL_USUARIO, NOME_USUARIO 
                FROM USUARIOS 
                WHERE EMAIL_USUARIO ILIKE @Email";

            command.Parameters.AddWithValue("Email", email);

            await using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var nomeOriginal = reader.GetString(2);

                // Formata o nome com primeira letra maiúscula em cada palavra
                var nomeFormatado = string.Join(' ',
                    nomeOriginal
                        .ToLower()
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => char.ToUpper(p[0]) + p.Substring(1))
                );

                return new UsuarioEmail
                {
                    Id = reader.GetInt32(0),
                    Email = reader.GetString(1),
                    Nome = nomeFormatado
                };
            }

            return null;
        }
    }
}


