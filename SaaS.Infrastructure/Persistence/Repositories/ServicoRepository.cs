using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class ServicoRepository : IServicoRepository
    {
        private readonly string _connectionString;

        public ServicoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<ServicoModel> InserirServico(ServicoModel servico)
        {
            ValidarDadosServico(servico);

            if (servico.UsuarioFk == 0)
                servico.UsuarioFk = null;

            if (servico.ProfissionalId == 0)
                servico.ProfissionalId = null;

            if (servico.EmpresaId == 0)
                servico.EmpresaId = null;

            var usuarioFk = ObterUsuarioResponsavel(servico);
            if (servico.TempoEstimadoMinutos <= 0)
                servico.TempoEstimadoMinutos = 30;

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                var tipoUsuario = await ObterTipoUsuario(connection, usuarioFk);
                if (tipoUsuario is not (TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO))
                    throw new ArgumentException("Servicos podem ser cadastrados apenas para EMPRESA ou AUTONOMO.");

                var possuiImagemServico = await ColunaImagemServicoExiste(connection);
                if (!possuiImagemServico && !string.IsNullOrWhiteSpace(servico.ImagemServico))
                    throw new ArgumentException("A migration de imagem dos servicos ainda nao foi aplicada.");

                var query = possuiImagemServico
                    ? @"
                    INSERT INTO SERVICOS (USUARIO_FK, NOME_SERVICO, VALOR_SERVICO, TEMPO_ESTIMADO_MINUTOS, IMAGEM_SERVICO)
                    VALUES (@UsuarioFk, @Nome, @Valor, @TempoEstimadoMinutos, @ImagemServico)
                    RETURNING ID_SERVICO"
                    : @"
                    INSERT INTO SERVICOS (USUARIO_FK, NOME_SERVICO, VALOR_SERVICO, TEMPO_ESTIMADO_MINUTOS)
                    VALUES (@UsuarioFk, @Nome, @Valor, @TempoEstimadoMinutos)
                    RETURNING ID_SERVICO";

                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@UsuarioFk", usuarioFk);
                    command.Parameters.AddWithValue("@Nome", servico.NomeServico);
                    command.Parameters.AddWithValue("@Valor", servico.Valor);
                    command.Parameters.AddWithValue("@TempoEstimadoMinutos", servico.TempoEstimadoMinutos);
                    if (possuiImagemServico)
                        command.Parameters.AddWithValue("@ImagemServico", (object?)servico.ImagemServico ?? DBNull.Value);

                    var id = await command.ExecuteScalarAsync();
                    servico.Id = Convert.ToInt32(id);
                    servico.UsuarioFk = usuarioFk;
                    servico.ProfissionalId = tipoUsuario == TipoUsuario.AUTONOMO ? usuarioFk : null;
                    servico.EmpresaId = tipoUsuario == TipoUsuario.EMPRESA ? usuarioFk : null;
                    return servico;
                }
            }
        }

        public async Task<List<ServicoModel>> ListarServicos(int id)
        {
            bool idInformado = id > 0;

            if (!idInformado)
                throw new ArgumentException("Informe um ID valido para buscar os servicos.");

            var servicos = new List<ServicoModel>();

            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var tipoUsuarioConsulta = await ObterTipoUsuario(connection, id);

                var query = tipoUsuarioConsulta == TipoUsuario.PROFISSIONAL
                    ? @"
                            SELECT DISTINCT
                                S.ID_SERVICO,
                                S.USUARIO_FK,
                                PS.EMPRESA_FK,
                                S.NOME_SERVICO,
                                S.VALOR_SERVICO,
                                S.TEMPO_ESTIMADO_MINUTOS,
                                COALESCE(to_jsonb(S)->>'imagem_servico', '') AS IMAGEM_SERVICO,
                                U.TIPO_USUARIO
                            FROM PROFISSIONAL_SERVICOS PS
                            INNER JOIN SERVICOS S ON S.ID_SERVICO = PS.SERVICO_FK
                            INNER JOIN USUARIOS U ON U.ID_USUARIO = S.USUARIO_FK
                            INNER JOIN VINCULOS V
                                    ON V.PROFISSIONAL_FK = PS.PROFISSIONAL_FK
                                   AND V.EMPRESA_FK = PS.EMPRESA_FK
                                   AND V.STATUS_SOLICITACAO = 'APROVADO'
                            WHERE PS.PROFISSIONAL_FK = @Id
                            ORDER BY S.NOME_SERVICO"
                    : @"
                            SELECT
                                S.ID_SERVICO,
                                S.USUARIO_FK,
                                NULL::INTEGER AS EMPRESA_FK,
                                S.NOME_SERVICO,
                                S.VALOR_SERVICO,
                                S.TEMPO_ESTIMADO_MINUTOS,
                                COALESCE(to_jsonb(S)->>'imagem_servico', '') AS IMAGEM_SERVICO,
                                U.TIPO_USUARIO
                            FROM SERVICOS S
                            INNER JOIN USUARIOS U ON U.ID_USUARIO = S.USUARIO_FK
                            WHERE S.USUARIO_FK = @Id
                            ORDER BY S.NOME_SERVICO";

                if (tipoUsuarioConsulta is TipoUsuario.CLIENTE)
                    throw new ArgumentException("Cliente nao possui servicos vinculados.");

                using (var command = new NpgsqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Id", id);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var usuarioFk = reader.GetInt32(reader.GetOrdinal("USUARIO_FK"));
                            var tipoUsuario = reader.GetString(reader.GetOrdinal("TIPO_USUARIO")).ToUpperInvariant();
                            var empresaFkOrdinal = reader.GetOrdinal("EMPRESA_FK");

                            servicos.Add(new ServicoModel
                            {
                                Id = reader.GetInt32(reader.GetOrdinal("ID_SERVICO")),
                                UsuarioFk = usuarioFk,
                                ProfissionalId = tipoUsuarioConsulta == TipoUsuario.PROFISSIONAL
                                    ? id
                                    : tipoUsuario == "AUTONOMO" ? usuarioFk : null,
                                EmpresaId = tipoUsuarioConsulta == TipoUsuario.PROFISSIONAL
                                    ? reader.GetInt32(empresaFkOrdinal)
                                    : tipoUsuario == "EMPRESA" ? usuarioFk : null,
                                NomeServico = reader.GetString(reader.GetOrdinal("NOME_SERVICO")),
                                Valor = reader.GetDecimal(reader.GetOrdinal("VALOR_SERVICO")),
                                TempoEstimadoMinutos = reader.GetInt32(reader.GetOrdinal("TEMPO_ESTIMADO_MINUTOS")),
                                ImagemServico = reader.GetString(reader.GetOrdinal("IMAGEM_SERVICO"))
                            });
                        }
                    }
                }
            }

            return servicos;
        }

        public async Task<ServicoModel?> AtualizarServico(
            int servicoId,
            int usuarioId,
            ServicoModel servico,
            bool atualizarImagem)
        {
            if (servicoId <= 0 || usuarioId <= 0)
                throw new ArgumentException("Informe um servico valido para atualizar.");

            ValidarDadosServico(servico);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var possuiImagemServico = await ColunaImagemServicoExiste(connection);
            if (atualizarImagem && !possuiImagemServico)
                throw new ArgumentException("A migration de imagem dos servicos ainda nao foi aplicada.");

            var query = possuiImagemServico
                ? @"
                    UPDATE SERVICOS
                    SET NOME_SERVICO = @Nome,
                        VALOR_SERVICO = @Valor,
                        TEMPO_ESTIMADO_MINUTOS = @TempoEstimadoMinutos,
                        IMAGEM_SERVICO = CASE
                            WHEN @AtualizarImagem THEN @ImagemServico
                            ELSE IMAGEM_SERVICO
                        END
                    WHERE ID_SERVICO = @ServicoId
                      AND USUARIO_FK = @UsuarioId
                    RETURNING ID_SERVICO, USUARIO_FK, NOME_SERVICO,
                              VALOR_SERVICO, TEMPO_ESTIMADO_MINUTOS,
                              COALESCE(IMAGEM_SERVICO, '')"
                : @"
                    UPDATE SERVICOS
                    SET NOME_SERVICO = @Nome,
                        VALOR_SERVICO = @Valor,
                        TEMPO_ESTIMADO_MINUTOS = @TempoEstimadoMinutos
                    WHERE ID_SERVICO = @ServicoId
                      AND USUARIO_FK = @UsuarioId
                    RETURNING ID_SERVICO, USUARIO_FK, NOME_SERVICO,
                              VALOR_SERVICO, TEMPO_ESTIMADO_MINUTOS,
                              ''::TEXT";

            await using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@ServicoId", servicoId);
            command.Parameters.AddWithValue("@UsuarioId", usuarioId);
            command.Parameters.AddWithValue("@Nome", servico.NomeServico.Trim());
            command.Parameters.AddWithValue("@Valor", servico.Valor);
            command.Parameters.AddWithValue("@TempoEstimadoMinutos", servico.TempoEstimadoMinutos);

            if (possuiImagemServico)
            {
                command.Parameters.AddWithValue("@AtualizarImagem", atualizarImagem);
                command.Parameters.AddWithValue(
                    "@ImagemServico",
                    (object?)servico.ImagemServico ?? DBNull.Value);
            }

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            return new ServicoModel
            {
                Id = reader.GetInt32(0),
                UsuarioFk = reader.GetInt32(1),
                ProfissionalId = servico.ProfissionalId,
                EmpresaId = servico.EmpresaId,
                NomeServico = reader.GetString(2),
                Valor = reader.GetDecimal(3),
                TempoEstimadoMinutos = reader.GetInt32(4),
                ImagemServico = reader.GetString(5)
            };
        }

        private static async Task<bool> ColunaImagemServicoExiste(NpgsqlConnection connection)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND table_name = 'servicos'
                      AND column_name = 'imagem_servico'
                )", connection);

            return Convert.ToBoolean(await command.ExecuteScalarAsync());
        }

        private static int ObterUsuarioResponsavel(ServicoModel servico)
        {
            if (servico.UsuarioFk.HasValue)
                return servico.UsuarioFk.Value;

            var profissionalInformado = servico.ProfissionalId.HasValue;
            var empresaInformada = servico.EmpresaId.HasValue;

            if (profissionalInformado == empresaInformada)
                throw new ArgumentException("Informe UsuarioFk ou apenas ProfissionalId/EmpresaId.");

            return servico.ProfissionalId ?? servico.EmpresaId!.Value;
        }

        private static void ValidarDadosServico(ServicoModel servico)
        {
            if (string.IsNullOrWhiteSpace(servico.NomeServico))
                throw new ArgumentException("Informe o nome do servico.");

            if (servico.Valor <= 0)
                throw new ArgumentException("Informe um valor valido para o servico.");

            if (servico.TempoEstimadoMinutos <= 0)
                throw new ArgumentException("Informe uma duracao valida para o servico.");
        }

        private static async Task<TipoUsuario> ObterTipoUsuario(NpgsqlConnection connection, int usuarioId)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT TIPO_USUARIO
                FROM USUARIOS
                WHERE ID_USUARIO = @UsuarioId", connection);

            command.Parameters.AddWithValue("@UsuarioId", usuarioId);

            var result = await command.ExecuteScalarAsync();
            if (result == null)
                throw new ArgumentException("Usuario responsavel pelo servico nao encontrado.");

            return TipoUsuarioExtensions.FromDatabaseValue(result.ToString());
        }
    }
}
