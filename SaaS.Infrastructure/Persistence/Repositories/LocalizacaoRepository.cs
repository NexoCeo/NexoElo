using Microsoft.Extensions.Configuration;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Models;
using Npgsql;
using SaaS.Infrastructure.Services;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class LocalizacaoRepository : ILocalizacaoRepository
    {
        private readonly string _connectionString;

        public LocalizacaoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string DefaultConnection nao encontrada.");
        }

        public async Task<List<PaisModel>> ListarPaises()
        {
            var paises = new List<PaisModel>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT ID_PAIS, NOME_PAIS
                FROM PAISES
                ORDER BY NOME_PAIS";

            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                paises.Add(new PaisModel
                {
                    Id = reader.GetInt32(reader.GetOrdinal("ID_PAIS")),
                    Nome = reader.GetString(reader.GetOrdinal("NOME_PAIS"))
                });
            }

            return paises;
        }

        public async Task<List<EstadoModel>> ListarEstadosPorPais(int paisId)
        {
            var estados = new List<EstadoModel>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT ID_ESTADO, NOME_ESTADO, PAIS_FK
                FROM ESTADOS
                WHERE PAIS_FK = @PaisId
                ORDER BY NOME_ESTADO";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@PaisId", paisId);

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                estados.Add(new EstadoModel
                {
                    Id = reader.GetInt32(reader.GetOrdinal("ID_ESTADO")),
                    Nome = reader.GetString(reader.GetOrdinal("NOME_ESTADO")),
                    PaisFk = reader.GetInt32(reader.GetOrdinal("PAIS_FK"))
                });
            }

            return estados;
        }

        public async Task<List<CidadeModel>> ListarCidadesPorEstado(int estadoId)
        {
            var cidades = new List<CidadeModel>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT ID_CIDADE, NOME_CIDADE, ESTADO_FK
                FROM CIDADES
                WHERE ESTADO_FK = @EstadoId
                ORDER BY NOME_CIDADE";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@EstadoId", estadoId);

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                cidades.Add(new CidadeModel
                {
                    Id = reader.GetInt32(reader.GetOrdinal("ID_CIDADE")),
                    Nome = reader.GetString(reader.GetOrdinal("NOME_CIDADE")),
                    EstadoFk = reader.GetInt32(reader.GetOrdinal("ESTADO_FK"))
                });
            }

            return cidades;
        }

        public async Task<bool> CidadeExiste(int cidadeId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = "SELECT EXISTS (SELECT 1 FROM CIDADES WHERE ID_CIDADE = @CidadeId)";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@CidadeId", cidadeId);

            return Convert.ToBoolean(await command.ExecuteScalarAsync());
        }

        public async Task<LocalizacaoResolvidaModel?> ResolverLocalizacaoAdministrativa(
            EnderecoGeocodificadoModel endereco)
        {
            var pais = (await ListarPaises())
                .FirstOrDefault(item => LocalizacaoNomeNormalizer.Equivale(item.Nome, endereco.Paises));
            if (pais == null)
                return null;

            var estado = (await ListarEstadosPorPais(pais.Id))
                .FirstOrDefault(item => LocalizacaoNomeNormalizer.Equivale(item.Nome, endereco.Estados));
            if (estado == null)
                return null;

            var cidade = (await ListarCidadesPorEstado(estado.Id))
                .FirstOrDefault(item => LocalizacaoNomeNormalizer.Equivale(item.Nome, endereco.Cidades));
            if (cidade == null)
                return null;

            return new LocalizacaoResolvidaModel
            {
                PaisId = pais.Id,
                PaisNome = pais.Nome,
                EstadoId = estado.Id,
                EstadoNome = estado.Nome,
                CidadeId = cidade.Id,
                CidadeNome = cidade.Nome
            };
        }
    }
}
