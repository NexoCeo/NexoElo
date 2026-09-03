using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Persistence.Repositories;

public class RecuperacaoSenhaRepository : IRecuperacaoSenhaRepository
{
    private readonly string _connectionString;
    private readonly IPasswordHasher<UsuarioModel> _passwordHasher;

    public RecuperacaoSenhaRepository(
        IConfiguration configuration,
        IPasswordHasher<UsuarioModel> passwordHasher)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string DefaultConnection nao encontrada.");
        _passwordHasher = passwordHasher;
    }

    public async Task<RecuperacaoSenhaModel?> ValidarCodigoAsync(int usuarioId, string codigoHash)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            SELECT ID_RECUPERACAO, USUARIO_FK, CODIGO_HASH, DATA_EXPIRACAO, USADO
            FROM RECUPERACAO_SENHA
            WHERE USUARIO_FK = @UsuarioId
              AND CODIGO_HASH = @CodigoHash
              AND USADO = FALSE
              AND CODIGO_VALIDADO = FALSE
              AND TENTATIVAS < 5
              AND DATA_EXPIRACAO > CURRENT_TIMESTAMP
            ORDER BY ID_RECUPERACAO DESC
            LIMIT 1", connection);
        command.Parameters.AddWithValue("@UsuarioId", usuarioId);
        command.Parameters.AddWithValue("@CodigoHash", codigoHash);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return new RecuperacaoSenhaModel
        {
            Id = reader.GetInt32(reader.GetOrdinal("ID_RECUPERACAO")),
            UsuarioFk = reader.GetInt32(reader.GetOrdinal("USUARIO_FK")),
            Codigo = reader.GetString(reader.GetOrdinal("CODIGO_HASH")),
            DataExpiracao = reader.GetDateTime(reader.GetOrdinal("DATA_EXPIRACAO")),
            Usado = reader.GetBoolean(reader.GetOrdinal("USADO"))
        };
    }

    public async Task RegistrarTentativaInvalidaAsync(int usuarioId)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            UPDATE RECUPERACAO_SENHA
            SET TENTATIVAS = TENTATIVAS + 1
            WHERE ID_RECUPERACAO = (
                SELECT ID_RECUPERACAO
                FROM RECUPERACAO_SENHA
                WHERE USUARIO_FK = @UsuarioId
                  AND USADO = FALSE
                  AND CODIGO_VALIDADO = FALSE
                ORDER BY ID_RECUPERACAO DESC
                LIMIT 1
            )", connection);
        command.Parameters.AddWithValue("@UsuarioId", usuarioId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DefinirTokenResetAsync(
        int recuperacaoId,
        string tokenHash,
        DateTime dataExpiracao)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(@"
            UPDATE RECUPERACAO_SENHA
            SET CODIGO_VALIDADO = TRUE,
                TOKEN_RESET_HASH = @TokenHash,
                TOKEN_RESET_EXPIRACAO = @DataExpiracao
            WHERE ID_RECUPERACAO = @Id
              AND USADO = FALSE", connection);
        command.Parameters.AddWithValue("@TokenHash", tokenHash);
        command.Parameters.AddWithValue("@DataExpiracao", dataExpiracao);
        command.Parameters.AddWithValue("@Id", recuperacaoId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<bool> RedefinirSenhaAsync(string tokenHash, string novaSenha)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        int recuperacaoId;
        int usuarioId;
        await using (var localizar = new NpgsqlCommand(@"
            SELECT ID_RECUPERACAO, USUARIO_FK
            FROM RECUPERACAO_SENHA
            WHERE TOKEN_RESET_HASH = @TokenHash
              AND CODIGO_VALIDADO = TRUE
              AND USADO = FALSE
              AND TOKEN_RESET_EXPIRACAO > CURRENT_TIMESTAMP
            FOR UPDATE", connection, transaction))
        {
            localizar.Parameters.AddWithValue("@TokenHash", tokenHash);
            await using var reader = await localizar.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                await transaction.RollbackAsync();
                return false;
            }

            recuperacaoId = reader.GetInt32(reader.GetOrdinal("ID_RECUPERACAO"));
            usuarioId = reader.GetInt32(reader.GetOrdinal("USUARIO_FK"));
        }

        var usuario = new UsuarioModel { Id = usuarioId };
        var senhaHash = _passwordHasher.HashPassword(usuario, novaSenha);
        await using (var atualizarSenha = new NpgsqlCommand(@"
            UPDATE USUARIOS
            SET SENHA_USUARIO = @Senha,
                DATA_ALTERACAO_USUARIO = CURRENT_TIMESTAMP
            WHERE ID_USUARIO = @UsuarioId", connection, transaction))
        {
            atualizarSenha.Parameters.AddWithValue("@Senha", senhaHash);
            atualizarSenha.Parameters.AddWithValue("@UsuarioId", usuarioId);
            await atualizarSenha.ExecuteNonQueryAsync();
        }

        await using (var consumir = new NpgsqlCommand(@"
            UPDATE RECUPERACAO_SENHA
            SET USADO = TRUE,
                DATA_USO = CURRENT_TIMESTAMP
            WHERE ID_RECUPERACAO = @Id", connection, transaction))
        {
            consumir.Parameters.AddWithValue("@Id", recuperacaoId);
            await consumir.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return true;
    }
}
