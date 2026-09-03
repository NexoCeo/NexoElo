using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Services;

public sealed class PostgresArquivoUploadService : IArquivoUploadService
{
    private readonly string _connectionString;
    private static readonly HashSet<string> TiposConteudoPermitidos =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp"
        };

    public PostgresArquivoUploadService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "A string de conexao DefaultConnection nao foi configurada.");
    }

    public async Task GarantirEstruturaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS ARQUIVOS_UPLOAD (
                CAMINHO_ARQUIVO VARCHAR(255) PRIMARY KEY,
                TIPO_CONTEUDO VARCHAR(100) NOT NULL,
                CONTEUDO BYTEA NOT NULL,
                DATA_CRIACAO TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> SalvarAsync(
        byte[] conteudo,
        string extensao,
        string tipoConteudo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        if (conteudo.Length == 0)
            throw new ArgumentException("O arquivo enviado esta vazio.", nameof(conteudo));

        var extensaoNormalizada = NormalizarExtensao(extensao);
        if (!TiposConteudoPermitidos.Contains(tipoConteudo))
            throw new ArgumentException("Tipo de conteudo de imagem nao permitido.", nameof(tipoConteudo));

        var caminho = $"uploads/{Guid.NewGuid():N}{extensaoNormalizada}";

        const string sql = """
            INSERT INTO ARQUIVOS_UPLOAD
                (CAMINHO_ARQUIVO, TIPO_CONTEUDO, CONTEUDO)
            VALUES
                (@Caminho, @TipoConteudo, @Conteudo);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Caminho", caminho);
        command.Parameters.AddWithValue("@TipoConteudo", tipoConteudo);
        command.Parameters.AddWithValue("@Conteudo", NpgsqlDbType.Bytea, conteudo);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return caminho;
    }

    public async Task<ArquivoUploadModel?> ObterAsync(
        string caminho,
        CancellationToken cancellationToken = default)
    {
        var caminhoNormalizado = NormalizarCaminho(caminho);

        const string sql = """
            SELECT CAMINHO_ARQUIVO, TIPO_CONTEUDO, CONTEUDO
            FROM ARQUIVOS_UPLOAD
            WHERE CAMINHO_ARQUIVO = @Caminho;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Caminho", caminhoNormalizado);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new ArquivoUploadModel
        {
            Caminho = reader.GetString(0),
            TipoConteudo = reader.GetString(1),
            Conteudo = (byte[])reader[2]
        };
    }

    public async Task RemoverAsync(
        string? caminho,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(caminho) ||
            caminho.Equals("SEM_FOTO", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string caminhoNormalizado;
        try
        {
            caminhoNormalizado = NormalizarCaminho(caminho);
        }
        catch (ArgumentException)
        {
            return;
        }

        const string sql = """
            DELETE FROM ARQUIVOS_UPLOAD
            WHERE CAMINHO_ARQUIVO = @Caminho;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Caminho", caminhoNormalizado);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizarExtensao(string extensao)
    {
        var normalizada = extensao.Trim().ToLowerInvariant();
        if (!normalizada.StartsWith('.'))
            normalizada = $".{normalizada}";

        return normalizada is ".jpg" or ".jpeg" or ".png" or ".webp"
            ? normalizada
            : throw new ArgumentException("Extensao de imagem nao permitida.", nameof(extensao));
    }

    private static string NormalizarCaminho(string caminho)
    {
        var normalizado = caminho.Trim().Replace('\\', '/').TrimStart('/');
        if (!normalizado.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase) ||
            normalizado.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Caminho de upload invalido.", nameof(caminho));
        }

        return normalizado;
    }
}
