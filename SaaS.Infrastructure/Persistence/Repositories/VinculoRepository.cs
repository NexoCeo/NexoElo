using Microsoft.Extensions.Configuration;
using Npgsql;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class VinculoRepository : IVinculoRepository
    {
        private readonly string _connectionString;

        public VinculoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<bool> JaExisteSolicitacaoPendente(int profissionalId, int empresaId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT COUNT(*) FROM VINCULOS
                WHERE PROFISSIONAL_FK = @ProfissionalId
                  AND EMPRESA_FK = @EmpresaId
                  AND STATUS_SOLICITACAO = 'PENDENTE'";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@ProfissionalId", profissionalId);
            command.Parameters.AddWithValue("@EmpresaId", empresaId);

            var result = await command.ExecuteScalarAsync();
            var count = Convert.ToInt32(result);
            return count > 0;
        }

        public async Task CriarSolicitacaoVinculo(int profissionalId, int empresaId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                INSERT INTO VINCULOS (PROFISSIONAL_FK, EMPRESA_FK, STATUS_SOLICITACAO)
                SELECT @ProfissionalId, @EmpresaId, 'PENDENTE'
                WHERE EXISTS (
                    SELECT 1 FROM USUARIOS
                    WHERE ID_USUARIO = @ProfissionalId
                      AND TIPO_USUARIO = 'PROFISSIONAL'
                )
                  AND EXISTS (
                    SELECT 1 FROM USUARIOS
                    WHERE ID_USUARIO = @EmpresaId
                      AND TIPO_USUARIO = 'EMPRESA'
                )";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@ProfissionalId", profissionalId);
            command.Parameters.AddWithValue("@EmpresaId", empresaId);

            var rowsAffected = await command.ExecuteNonQueryAsync();
            if (rowsAffected == 0)
                throw new ArgumentException("Profissional ou empresa invalido para o vinculo.");
        }

        public async Task<List<ProfissionalEmpresaModel>> ListarProfissionaisPorEmpresa(int empresaId)
        {
            var profissionais = new List<ProfissionalEmpresaModel>();
        
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
        
            var query = @"
                WITH ULTIMO_VINCULO AS (
                    SELECT DISTINCT ON (PROFISSIONAL_FK)
                        ID_SOLICITACAO,
                        PROFISSIONAL_FK,
                        EMPRESA_FK,
                        STATUS_SOLICITACAO,
                        DATA_SOLICITACAO
                    FROM VINCULOS
                    WHERE EMPRESA_FK = @EmpresaId
                    ORDER BY
                        PROFISSIONAL_FK,
                        DATA_SOLICITACAO DESC,
                        ID_SOLICITACAO DESC
                )
                SELECT
                    U.ID_USUARIO,
                    U.NOME_USUARIO,
                    U.EMAIL_USUARIO,
                    U.TELEFONE_USUARIO,
                    U.FOTO_PERFIL,
                    U.CIDADE_FK,
                    V.STATUS_SOLICITACAO,
                    V.DATA_SOLICITACAO
                FROM ULTIMO_VINCULO V
                INNER JOIN USUARIOS U
                    ON U.ID_USUARIO = V.PROFISSIONAL_FK
                WHERE U.TIPO_USUARIO = 'PROFISSIONAL'
                  AND V.STATUS_SOLICITACAO = 'APROVADO'
                ORDER BY U.NOME_USUARIO";
        
            using var command = new NpgsqlCommand(query, connection);
        
            command.Parameters.AddWithValue(
                "@EmpresaId",
                empresaId
            );
        
            using var reader =
                await command.ExecuteReaderAsync();
        
            while (await reader.ReadAsync())
            {
                profissionais.Add(
                    new ProfissionalEmpresaModel
                    {
                        Id = reader.GetInt32(
                            reader.GetOrdinal("ID_USUARIO")
                        ),
        
                        Nome = reader.GetString(
                            reader.GetOrdinal("NOME_USUARIO")
                        ),
        
                        Email = reader.IsDBNull(
                            reader.GetOrdinal("EMAIL_USUARIO")
                        )
                            ? null
                            : reader.GetString(
                                reader.GetOrdinal("EMAIL_USUARIO")
                            ),
        
                        Telefone = reader.IsDBNull(
                            reader.GetOrdinal("TELEFONE_USUARIO")
                        )
                            ? null
                            : reader.GetString(
                                reader.GetOrdinal("TELEFONE_USUARIO")
                            ),
        
                        FotoPerfil = reader.IsDBNull(
                            reader.GetOrdinal("FOTO_PERFIL")
                        )
                            ? null
                            : reader.GetString(
                                reader.GetOrdinal("FOTO_PERFIL")
                            ),
        
                        CidadeFk = reader.IsDBNull(
                            reader.GetOrdinal("CIDADE_FK")
                        )
                            ? null
                            : reader.GetInt32(
                                reader.GetOrdinal("CIDADE_FK")
                            ),
        
                        VinculoStatus =
                            reader.GetString(
                                reader.GetOrdinal(
                                    "STATUS_SOLICITACAO"
                                )
                            ),
        
                        DataSolicitacao =
                            reader.IsDBNull(
                                reader.GetOrdinal(
                                    "DATA_SOLICITACAO"
                                )
                            )
                                ? null
                                : reader.GetDateTime(
                                    reader.GetOrdinal(
                                        "DATA_SOLICITACAO"
                                    )
                                )
                    }
                );
            }
        
            return profissionais;
        }
        public async Task<List<ServicoModel>> ListarServicosDoProfissional(int profissionalId, int empresaId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await ValidarVinculoProfissionalEmpresa(connection, null, profissionalId, empresaId);

            if (!await ExisteTabelaProfissionalServicos(connection))
                throw new InvalidOperationException(
                    "Tabela PROFISSIONAL_SERVICOS nao encontrada. Execute a migracao do banco de dados.");

            return await ListarServicosDoProfissional(connection, profissionalId, empresaId);
        }

        public async Task<List<ServicoModel>> VincularServicosProfissional(int profissionalId, int empresaId, IEnumerable<int> servicoIds)
        {
            var ids = servicoIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            if (!await ExisteTabelaProfissionalServicos(connection))
                throw new InvalidOperationException(
                    "Tabela PROFISSIONAL_SERVICOS nao encontrada. Execute a migracao do banco de dados.");

            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await ValidarVinculoProfissionalEmpresa(connection, transaction, profissionalId, empresaId);
                await ValidarServicosPertencemEmpresa(connection, transaction, empresaId, ids);

                await using var deleteCommand = new NpgsqlCommand(@"
                    DELETE FROM PROFISSIONAL_SERVICOS
                    WHERE PROFISSIONAL_FK = @ProfissionalId
                      AND EMPRESA_FK = @EmpresaId", connection, transaction);

                deleteCommand.Parameters.AddWithValue("@ProfissionalId", profissionalId);
                deleteCommand.Parameters.AddWithValue("@EmpresaId", empresaId);
                await deleteCommand.ExecuteNonQueryAsync();

                foreach (var servicoId in ids)
                {
                    await using var insertCommand = new NpgsqlCommand(@"
                        INSERT INTO PROFISSIONAL_SERVICOS (
                            PROFISSIONAL_FK,
                            EMPRESA_FK,
                            SERVICO_FK
                        )
                        VALUES (
                            @ProfissionalId,
                            @EmpresaId,
                            @ServicoId
                        )", connection, transaction);

                    insertCommand.Parameters.AddWithValue("@ProfissionalId", profissionalId);
                    insertCommand.Parameters.AddWithValue("@EmpresaId", empresaId);
                    insertCommand.Parameters.AddWithValue("@ServicoId", servicoId);
                    await insertCommand.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            return await ListarServicosDoProfissional(profissionalId, empresaId);
        }

        public async Task<(int ProfissionalId, int EmpresaId)> ObterIdsDaSolicitacaoAsync(int id)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "SELECT PROFISSIONAL_FK, EMPRESA_FK FROM VINCULOS WHERE ID_SOLICITACAO = @Id";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@Id", id);

            using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return (
                    reader.GetInt32(reader.GetOrdinal("PROFISSIONAL_FK")),
                    reader.GetInt32(reader.GetOrdinal("EMPRESA_FK"))
                );
            }

            throw new Exception("Solicitacao nao encontrada.");
        }

        public async Task AtualizarStatusSolicitacaoAsync(int id, string status)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = "UPDATE VINCULOS SET STATUS_SOLICITACAO = @Status WHERE ID_SOLICITACAO = @Id";

            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@Status", status);
            command.Parameters.AddWithValue("@Id", id);

            await command.ExecuteNonQueryAsync();
        }

        public async Task<string?> ObterStatusSolicitacaoPorProfissionalAsync(int profissionalId)
        {
            using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var query = @"
                SELECT STATUS_SOLICITACAO
                FROM VINCULOS
                WHERE PROFISSIONAL_FK = @ProfissionalId
                ORDER BY DATA_SOLICITACAO DESC
                LIMIT 1";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@ProfissionalId", profissionalId);

            var result = await command.ExecuteScalarAsync();
            return result?.ToString();
        }
        
        public async Task<IEnumerable<SolicitacaoVinculoModel>> ListarSolicitacoesPendentesPorEmpresa(int empresaId)
        {
            await using var connection =
                new NpgsqlConnection(_connectionString);
        
            await connection.OpenAsync();
        
            const string sql = @"
                SELECT
                    V.ID_SOLICITACAO,
                    V.PROFISSIONAL_FK,
                    U.NOME_USUARIO,
                    U.EMAIL_USUARIO,
                    U.TELEFONE_USUARIO,
                    V.STATUS_SOLICITACAO,
                    V.DATA_SOLICITACAO
                FROM VINCULOS V
                INNER JOIN USUARIOS U
                    ON U.ID_USUARIO = V.PROFISSIONAL_FK
                WHERE V.EMPRESA_FK = @EmpresaId
                  AND V.STATUS_SOLICITACAO = 'PENDENTE'
                  AND U.TIPO_USUARIO = 'PROFISSIONAL'
                ORDER BY
                    V.DATA_SOLICITACAO DESC,
                    V.ID_SOLICITACAO DESC;
            ";
        
            await using var command =
                new NpgsqlCommand(sql, connection);
        
            command.Parameters.AddWithValue(
                "@EmpresaId",
                empresaId
            );
        
            var solicitacoes =
                new List<SolicitacaoVinculoModel>();
        
            await using var reader =
                await command.ExecuteReaderAsync();
        
            while (await reader.ReadAsync())
            {
                solicitacoes.Add(
                    new SolicitacaoVinculoModel
                    {
                        Id = reader.GetInt32(
                            reader.GetOrdinal(
                                "ID_SOLICITACAO"
                            )
                        ),
        
                        ProfissionalId =
                            reader.GetInt32(
                                reader.GetOrdinal(
                                    "PROFISSIONAL_FK"
                                )
                            ),
        
                        ProfissionalNome =
                            reader.IsDBNull(
                                reader.GetOrdinal(
                                    "NOME_USUARIO"
                                )
                            )
                                ? null
                                : reader.GetString(
                                    reader.GetOrdinal(
                                        "NOME_USUARIO"
                                    )
                                ),
        
                        ProfissionalEmail =
                            reader.IsDBNull(
                                reader.GetOrdinal(
                                    "EMAIL_USUARIO"
                                )
                            )
                                ? null
                                : reader.GetString(
                                    reader.GetOrdinal(
                                        "EMAIL_USUARIO"
                                    )
                                ),
        
                        ProfissionalTelefone =
                            reader.IsDBNull(
                                reader.GetOrdinal(
                                    "TELEFONE_USUARIO"
                                )
                            )
                                ? null
                                : reader.GetString(
                                    reader.GetOrdinal(
                                        "TELEFONE_USUARIO"
                                    )
                                ),
        
                        VinculoStatus =
                            reader.GetString(
                                reader.GetOrdinal(
                                    "STATUS_SOLICITACAO"
                                )
                            ),
        
                        DataSolicitacao =
                            reader.IsDBNull(
                                reader.GetOrdinal(
                                    "DATA_SOLICITACAO"
                                )
                            )
                                ? null
                                : reader.GetDateTime(
                                    reader.GetOrdinal(
                                        "DATA_SOLICITACAO"
                                    )
                                )
                    }
                );
            }
        
            return solicitacoes;
        }

        public async Task<bool> ResponderSolicitacaoAsync(int solicitacaoId,int empresaId,string status)
        {
            await using var connection =
                new NpgsqlConnection(_connectionString);
        
            await connection.OpenAsync();
        
            const string sql = @"
                UPDATE VINCULOS
                SET STATUS_SOLICITACAO = @Status
                WHERE ID_SOLICITACAO = @SolicitacaoId
                  AND EMPRESA_FK = @EmpresaId
                  AND STATUS_SOLICITACAO = 'PENDENTE';
            ";
        
            await using var command =
                new NpgsqlCommand(sql, connection);
        
            command.Parameters.AddWithValue(
                "@Status",
                status
            );
        
            command.Parameters.AddWithValue(
                "@SolicitacaoId",
                solicitacaoId
            );
        
            command.Parameters.AddWithValue(
                "@EmpresaId",
                empresaId
            );
        
            var linhasAfetadas =
                await command.ExecuteNonQueryAsync();
        
            return linhasAfetadas > 0;
        }

        public async Task<VinculoProfissionalModel?> ObterVinculoAtualDoProfissionalAsync(int profissionalId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var command = new NpgsqlCommand(@"
            SELECT
                V.ID_SOLICITACAO,
                V.PROFISSIONAL_FK,
                V.EMPRESA_FK,
                E.NOME_FANTASIA,
                U.NOME_USUARIO,
                V.STATUS_SOLICITACAO,
                V.DATA_SOLICITACAO
            FROM VINCULOS V
            INNER JOIN EMPRESAS E
                ON E.USUARIO_FK = V.EMPRESA_FK
            INNER JOIN USUARIOS U
                ON U.ID_USUARIO = E.USUARIO_FK
            WHERE V.PROFISSIONAL_FK = @ProfissionalId
            ORDER BY
                V.DATA_SOLICITACAO DESC,
                V.ID_SOLICITACAO DESC
            LIMIT 1",
            connection);

            command.Parameters.AddWithValue(
                "@ProfissionalId",
                profissionalId);

            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
                return null;

            var nomeFantasia = reader["NOME_FANTASIA"] as string;
            var nomeUsuario = reader["NOME_USUARIO"] as string;

            return new VinculoProfissionalModel
            {
               Id = 
                   reader.GetInt32(reader.GetOrdinal("ID_SOLICITACAO")),

                ProfissionalId =
                    reader.GetInt32(reader.GetOrdinal("PROFISSIONAL_FK")),

                EmpresaId =
                    reader.GetInt32(reader.GetOrdinal("EMPRESA_FK")),

                EmpresaNome =
                    !string.IsNullOrWhiteSpace(nomeFantasia)
                        ? nomeFantasia
                        : nomeUsuario,

                VinculoStatus =
                    reader["STATUS_SOLICITACAO"]?.ToString()
                    ?? string.Empty,

                DataSolicitacao =
                    reader["DATA_SOLICITACAO"] == DBNull.Value
                        ? null
                        : Convert.ToDateTime(reader["DATA_SOLICITACAO"])
            };
        }

        public async Task<bool> PossuiVinculoAprovadoAsync(int profissionalId)
        {
            if (profissionalId <= 0)
                return false;

            await using var connection =
                new NpgsqlConnection(_connectionString);

            await connection.OpenAsync();

            const string sql = @"
            SELECT EXISTS (
                SELECT 1
                FROM VINCULOS
                WHERE PROFISSIONAL_FK = @ProfissionalId
                  AND STATUS_SOLICITACAO = 'APROVADO'
            );";

            await using var command =
                new NpgsqlCommand(sql, connection);

            command.Parameters.AddWithValue(
                "@ProfissionalId",
                profissionalId);

            var resultado = await command.ExecuteScalarAsync();

            return resultado != null &&
                   resultado != DBNull.Value &&
                   Convert.ToBoolean(resultado);
        }

        private static async Task ValidarVinculoProfissionalEmpresa(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            int profissionalId,
            int empresaId)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT 1
                FROM USUARIOS P
                INNER JOIN USUARIOS E ON E.ID_USUARIO = @EmpresaId
                WHERE P.ID_USUARIO = @ProfissionalId
                  AND P.TIPO_USUARIO = 'PROFISSIONAL'
                  AND E.TIPO_USUARIO = 'EMPRESA'
                  AND EXISTS (
                      SELECT 1
                      FROM VINCULOS V
                      WHERE V.PROFISSIONAL_FK = P.ID_USUARIO
                        AND V.EMPRESA_FK = E.ID_USUARIO
                        AND V.STATUS_SOLICITACAO = 'APROVADO'
                  )
                LIMIT 1", connection, transaction);

            command.Parameters.AddWithValue("@ProfissionalId", profissionalId);
            command.Parameters.AddWithValue("@EmpresaId", empresaId);

            var result = await command.ExecuteScalarAsync();
            if (result == null)
                throw new ArgumentException("Profissional precisa ter vinculo APROVADO com a empresa.");
        }

        private static async Task ValidarServicosPertencemEmpresa(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int empresaId,
            List<int> servicoIds)
        {
            if (servicoIds.Count == 0)
                return;

            await using var command = new NpgsqlCommand(@"
                SELECT ID_SERVICO
                FROM SERVICOS
                WHERE USUARIO_FK = @EmpresaId
                  AND ID_SERVICO = ANY(@ServicoIds)", connection, transaction);

            command.Parameters.AddWithValue("@EmpresaId", empresaId);
            command.Parameters.AddWithValue("@ServicoIds", servicoIds.ToArray());

            var encontrados = new HashSet<int>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                encontrados.Add(reader.GetInt32(reader.GetOrdinal("ID_SERVICO")));
            }

            var invalidos = servicoIds.Where(id => !encontrados.Contains(id)).ToList();
            if (invalidos.Count > 0)
                throw new ArgumentException("Todos os servicos vinculados ao profissional precisam pertencer a empresa.");
        }

        private static async Task<List<ServicoModel>> ListarServicosDoProfissional(
            NpgsqlConnection connection,
            int profissionalId,
            int empresaId)
        {
            var servicos = new List<ServicoModel>();

            await using var command = new NpgsqlCommand(@"
                SELECT
                    S.ID_SERVICO,
                    S.USUARIO_FK AS SERVICO_USUARIO_FK,
                    S.NOME_SERVICO,
                    S.VALOR_SERVICO,
                    S.TEMPO_ESTIMADO_MINUTOS,
                    COALESCE(to_jsonb(S)->>'imagem_servico', '') AS IMAGEM_SERVICO
                FROM PROFISSIONAL_SERVICOS PS
                INNER JOIN SERVICOS S ON S.ID_SERVICO = PS.SERVICO_FK
                WHERE PS.PROFISSIONAL_FK = @ProfissionalId
                  AND PS.EMPRESA_FK = @EmpresaId
                  AND S.USUARIO_FK = @EmpresaId
                ORDER BY S.NOME_SERVICO", connection);

            command.Parameters.AddWithValue("@ProfissionalId", profissionalId);
            command.Parameters.AddWithValue("@EmpresaId", empresaId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                servicos.Add(MapServico(reader));
            }

            return servicos;
        }

        private static async Task<bool> ExisteTabelaProfissionalServicos(NpgsqlConnection connection)
        {
            await using var command = new NpgsqlCommand(
                "SELECT to_regclass('public.profissional_servicos') IS NOT NULL",
                connection);

            return Convert.ToBoolean(await command.ExecuteScalarAsync());
        }

        private static ServicoModel MapServico(NpgsqlDataReader reader)
        {
            var usuarioFk = reader.GetInt32(reader.GetOrdinal("SERVICO_USUARIO_FK"));

            return new ServicoModel
            {
                Id = reader.GetInt32(reader.GetOrdinal("ID_SERVICO")),
                UsuarioFk = usuarioFk,
                EmpresaId = usuarioFk,
                ProfissionalId = null,
                NomeServico = reader.GetString(reader.GetOrdinal("NOME_SERVICO")),
                Valor = reader.GetDecimal(reader.GetOrdinal("VALOR_SERVICO")),
                TempoEstimadoMinutos = reader.GetInt32(reader.GetOrdinal("TEMPO_ESTIMADO_MINUTOS")),
                ImagemServico = reader.GetString(reader.GetOrdinal("IMAGEM_SERVICO"))
            };
        }
    }
}

