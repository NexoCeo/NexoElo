using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Exceptions;
using SaaS.Domain.Models;
using SaaS.Domain.Rules;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class AgendamentoRepository : IAgendamentoRepository
    {
        private const int IntervaloSugestaoMinutos = 15;
        private const int DiasBuscaSugestao = 60;
        private readonly string _connectionString;

        public AgendamentoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<AgendamentoModel> CriarAgendamentoAsync(AgendamentoModel agendamento)
        {
            ValidarDadosBasicos(agendamento);
            agendamento.DataAgendamento = NormalizarDataAgendamento(agendamento.DataAgendamento);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var responsavelId = ObterResponsavelId(agendamento);
                var prestadorId = ObterPrestadorId(agendamento);
                var clienteUsuarioId = agendamento.ClienteId;
                var clienteFk = await ObterClienteFk(connection, transaction, agendamento.ClienteId);

                await AdquirirBloqueioCliente(connection, transaction, clienteFk);

                if (await ExisteAgendamentoAtivoClienteNaData(
                        connection,
                        transaction,
                        clienteFk,
                        agendamento.DataAgendamento.Date))
                {
                    throw new RegraAgendamentoException(
                        "O cliente já possui um agendamento ativo neste dia. Conclua ou cancele o agendamento anterior para agendar novamente.");
                }

                var servico = await ObterServico(
                    connection,
                    transaction,
                    agendamento.ServicoId,
                    responsavelId);

                await ValidarResponsavelEPrestador(
                    connection,
                    transaction,
                    agendamento,
                    responsavelId,
                    prestadorId);

                // Serializa a validacao e a gravacao por prestador. Assim, duas requisicoes
                // simultaneas nao conseguem reservar intervalos sobrepostos.
                await AdquirirBloqueioPrestador(connection, transaction, prestadorId);

                var funcionamento = await ObterFuncionamentoResponsavel(
                    connection,
                    transaction,
                    responsavelId);

                var inicioAgendamento = agendamento.DataAgendamento;
                var fimAgendamento = inicioAgendamento.AddMinutes(servico.DuracaoMinutos);
                var agora = ObterAgoraSaoPaulo();

                if (inicioAgendamento <= agora)
                {
                    throw await CriarIndisponibilidade(
                        connection,
                        transaction,
                        prestadorId,
                        funcionamento,
                        servico.DuracaoMinutos,
                        inicioAgendamento,
                        "Selecione uma data e um horario futuros.");
                }

                if (ObterIntervaloFuncionamento(
                        funcionamento,
                        inicioAgendamento,
                        servico.DuracaoMinutos) == null)
                {
                    throw await CriarIndisponibilidade(
                        connection,
                        transaction,
                        prestadorId,
                        funcionamento,
                        servico.DuracaoMinutos,
                        inicioAgendamento,
                        "O horario solicitado ou o termino do servico esta fora do funcionamento configurado.");
                }

                if (await ExisteConflito(
                        connection,
                        transaction,
                        prestadorId,
                        inicioAgendamento,
                        fimAgendamento))
                {
                    throw await CriarIndisponibilidade(
                        connection,
                        transaction,
                        prestadorId,
                        funcionamento,
                        servico.DuracaoMinutos,
                        inicioAgendamento,
                        "O profissional ja possui um agendamento nesse intervalo.");
                }

                agendamento.Taxa = 0;
                agendamento.Valor = servico.Valor;
                agendamento.ValorServico = servico.Valor;

                await using var insertCmd = new NpgsqlCommand(@"
                    INSERT INTO AGENDAMENTOS
                        (CLIENTE_FK, PRESTADOR_FK, SERVICO_FK, DATA_AGENDAMENTO, STATUS_AGENDAMENTO)
                    VALUES
                        (@ClienteFk, @PrestadorFk, @ServicoFk, @DataAgendamento, @Status)
                    RETURNING ID_AGENDAMENTO", connection, transaction);

                insertCmd.Parameters.AddWithValue("@ClienteFk", clienteFk);
                insertCmd.Parameters.AddWithValue("@PrestadorFk", prestadorId);
                insertCmd.Parameters.AddWithValue("@ServicoFk", agendamento.ServicoId);
                insertCmd.Parameters.Add("@DataAgendamento", NpgsqlDbType.Timestamp).Value =
                    DateTime.SpecifyKind(inicioAgendamento, DateTimeKind.Unspecified);
                insertCmd.Parameters.AddWithValue("@Status", NormalizarStatus(agendamento.Status));

                var id = await insertCmd.ExecuteScalarAsync();
                agendamento.Id = Convert.ToInt32(id);
                agendamento.ClienteId = clienteUsuarioId;

                await transaction.CommitAsync();
                return agendamento;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<List<AgendamentoModel>> ListarAgendamentosDoDia(int usuarioId)
        {
            var hoje = ObterAgoraSaoPaulo().Date;

            return await ListarAgendamentosPorData(usuarioId, hoje);
        }

        public async Task<List<HorarioDisponivelModel>> ListarHorariosDisponiveis(
            int responsavelId,
            int? profissionalId,
            int servicoId,
            DateTime data)
        {
            if (responsavelId <= 0)
                throw new ArgumentException("Responsavel invalido.");

            if (servicoId <= 0)
                throw new ArgumentException("Servico invalido.");

            if (data == default)
                throw new ArgumentException("Data invalida.");

            var dataLocal = NormalizarDataAgendamento(data).Date;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var tipoResponsavel = await ObterTipoUsuario(
                    connection,
                    transaction,
                    responsavelId,
                    "Responsavel pelo agendamento nao encontrado.");

                int prestadorId;
                int? empresaId;

                if (tipoResponsavel == TipoUsuario.AUTONOMO)
                {
                    prestadorId = responsavelId;
                    empresaId = null;
                }
                else if (tipoResponsavel == TipoUsuario.EMPRESA)
                {
                    if (profissionalId.GetValueOrDefault() <= 0)
                        throw new ArgumentException("Selecione um profissional vinculado a empresa.");

                    prestadorId = profissionalId!.Value;
                    empresaId = responsavelId;
                }
                else
                {
                    throw new ArgumentException("Apenas empresa ou autonomo podem receber agendamentos.");
                }

                var contextoAgendamento = new AgendamentoModel
                {
                    EmpresaId = empresaId,
                    ProfissionalId = prestadorId,
                    ServicoId = servicoId,
                    DataAgendamento = dataLocal
                };

                var servico = await ObterServico(
                    connection,
                    transaction,
                    servicoId,
                    responsavelId);

                await ValidarResponsavelEPrestador(
                    connection,
                    transaction,
                    contextoAgendamento,
                    responsavelId,
                    prestadorId);

                var funcionamento = await ObterFuncionamentoResponsavel(
                    connection,
                    transaction,
                    responsavelId);

                var horarios = await ObterHorariosDisponiveisNaData(
                    connection,
                    transaction,
                    prestadorId,
                    funcionamento,
                    servico.DuracaoMinutos,
                    dataLocal);

                await transaction.CommitAsync();

                return horarios
                    .Select(horario => new HorarioDisponivelModel
                    {
                        DataAgendamento = horario,
                        Horario = horario.ToString("HH:mm")
                    })
                    .ToList();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<List<AgendamentoModel>> ListarAgendamentosPorData(int usuarioId,DateTime data,int? profissionalId = null)
        {
            if (usuarioId <= 0)
                throw new ArgumentException("Usuario invalido.");

            if (data == default)
                throw new ArgumentException("Data invalida.");

            if (profissionalId.HasValue && profissionalId.Value <= 0)
                throw new ArgumentException("Profissional invalido.");

            var inicio = DateTime.SpecifyKind(
                data.Date,
                DateTimeKind.Unspecified);

            var fim = inicio.AddDays(1);

            var sql = @"
            SELECT
                A.ID_AGENDAMENTO,
                A.CLIENTE_FK,
                C.USUARIO_FK AS CLIENTE_USUARIO_FK,
                A.PRESTADOR_FK,
                A.SERVICO_FK,
                A.DATA_AGENDAMENTO,
                A.STATUS_AGENDAMENTO,
                S.VALOR_SERVICO,
                S.NOME_SERVICO,
                S.USUARIO_FK AS RESPONSAVEL_FK,
                PU.TIPO_USUARIO,
                PU.NOME_USUARIO AS PROFISSIONAL_NOME,
                CU.NOME_USUARIO AS CLIENTE_NOME
            FROM AGENDAMENTOS A
            INNER JOIN SERVICOS S
                ON S.ID_SERVICO = A.SERVICO_FK
            LEFT JOIN CLIENTES C
                ON C.ID_CLIENTE = A.CLIENTE_FK
            LEFT JOIN USUARIOS CU
                ON CU.ID_USUARIO = C.USUARIO_FK
            INNER JOIN USUARIOS PU
                ON PU.ID_USUARIO = A.PRESTADOR_FK
            WHERE (
                A.PRESTADOR_FK = @UsuarioId
                OR S.USUARIO_FK = @UsuarioId
            )
              AND A.DATA_AGENDAMENTO >= @Inicio
              AND A.DATA_AGENDAMENTO < @Fim";

                if (profissionalId.HasValue)
                {
                    sql += @"
              AND A.PRESTADOR_FK = @ProfissionalId";
                }

                sql += @"
            ORDER BY A.DATA_AGENDAMENTO";

            return await ListarAgendamentos(sql, command =>
            {
                command.Parameters.AddWithValue("@UsuarioId", usuarioId);

                command.Parameters
                    .Add("@Inicio", NpgsqlDbType.Timestamp)
                    .Value = inicio;

                command.Parameters
                    .Add("@Fim", NpgsqlDbType.Timestamp)
                    .Value = fim;

                if (profissionalId.HasValue)
                {
                    command.Parameters.AddWithValue(
                        "@ProfissionalId",
                        profissionalId.Value);
                }
            });
        }

        public async Task<List<AgendamentoModel>> ListarHistoricoAgendamentos(int usuarioId)
        {
            const string sql = @"
                SELECT
                    A.ID_AGENDAMENTO,
                    A.CLIENTE_FK,
                    C.USUARIO_FK AS CLIENTE_USUARIO_FK,
                    A.PRESTADOR_FK,
                    A.SERVICO_FK,
                    A.DATA_AGENDAMENTO,
                    A.STATUS_AGENDAMENTO,
                    S.VALOR_SERVICO,
                    S.NOME_SERVICO,
                    S.USUARIO_FK AS RESPONSAVEL_FK,
                    PU.TIPO_USUARIO,
                    PU.NOME_USUARIO AS PROFISSIONAL_NOME,
                    CU.NOME_USUARIO AS CLIENTE_NOME
                FROM AGENDAMENTOS A
                INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
                LEFT JOIN CLIENTES C ON C.ID_CLIENTE = A.CLIENTE_FK
                LEFT JOIN USUARIOS CU ON CU.ID_USUARIO = C.USUARIO_FK
                INNER JOIN USUARIOS PU ON PU.ID_USUARIO = A.PRESTADOR_FK
                WHERE (
                    A.PRESTADOR_FK = @UsuarioId
                    OR S.USUARIO_FK = @UsuarioId
                    OR C.USUARIO_FK = @UsuarioId
                )
                ORDER BY A.DATA_AGENDAMENTO DESC";

            return await ListarAgendamentos(sql, command =>
            {
                command.Parameters.AddWithValue("@UsuarioId", usuarioId);
            });
        }

        public async Task<List<AgendamentoModel>> ListarAgendamentosPorPeriodo(int usuarioId,DateTime inicio,DateTime fim,int? profissionalId = null)
        {
            if (usuarioId <= 0)
                throw new ArgumentException("Usuario invalido.");

            if (inicio == default)
                throw new ArgumentException("Data inicial invalida.");

            if (fim == default)
                throw new ArgumentException("Data final invalida.");

            if (fim.Date < inicio.Date)
                throw new ArgumentException(
                    "A data final nao pode ser anterior a data inicial.");

            if (profissionalId.HasValue && profissionalId.Value <= 0)
                throw new ArgumentException("Profissional invalido.");

            var dataInicio = DateTime.SpecifyKind(
                inicio.Date,
                DateTimeKind.Unspecified);

            // Como queremos incluir o dia final inteiro,
            // avançamos para 00:00 do dia seguinte.
            var dataFim = DateTime.SpecifyKind(
                fim.Date.AddDays(1),
                DateTimeKind.Unspecified);

            var sql = @"
            SELECT
                A.ID_AGENDAMENTO,
                A.CLIENTE_FK,
                C.USUARIO_FK AS CLIENTE_USUARIO_FK,
                A.PRESTADOR_FK,
                A.SERVICO_FK,
                A.DATA_AGENDAMENTO,
                A.STATUS_AGENDAMENTO,
                S.VALOR_SERVICO,
                S.NOME_SERVICO,
                S.USUARIO_FK AS RESPONSAVEL_FK,
                PU.TIPO_USUARIO,
                PU.NOME_USUARIO AS PROFISSIONAL_NOME,
                CU.NOME_USUARIO AS CLIENTE_NOME
            FROM AGENDAMENTOS A
            INNER JOIN SERVICOS S
                ON S.ID_SERVICO = A.SERVICO_FK
            LEFT JOIN CLIENTES C
                ON C.ID_CLIENTE = A.CLIENTE_FK
            LEFT JOIN USUARIOS CU
                ON CU.ID_USUARIO = C.USUARIO_FK
            INNER JOIN USUARIOS PU
                ON PU.ID_USUARIO = A.PRESTADOR_FK
            WHERE (
                A.PRESTADOR_FK = @UsuarioId
                OR S.USUARIO_FK = @UsuarioId
            )
              AND A.DATA_AGENDAMENTO >= @Inicio
              AND A.DATA_AGENDAMENTO < @Fim";

                if (profissionalId.HasValue)
                {
                    sql += @"
              AND A.PRESTADOR_FK = @ProfissionalId";
                }

                sql += @"
            ORDER BY A.DATA_AGENDAMENTO";

            return await ListarAgendamentos(sql, command =>
            {
                command.Parameters.AddWithValue(
                    "@UsuarioId",
                    usuarioId);

                command.Parameters
                    .Add("@Inicio", NpgsqlDbType.Timestamp)
                    .Value = dataInicio;

                command.Parameters
                    .Add("@Fim", NpgsqlDbType.Timestamp)
                    .Value = dataFim;

                if (profissionalId.HasValue)
                {
                    command.Parameters.AddWithValue(
                        "@ProfissionalId",
                        profissionalId.Value);
                }
            });
        }
        public async Task<TipoUsuario> ObterTipoUsuarioAsync(int responsavelId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(
                "SELECT TIPO_USUARIO FROM USUARIOS WHERE ID_USUARIO = @ResponsavelId",
                connection);

            command.Parameters.AddWithValue("@ResponsavelId", responsavelId);

            var tipoUsuario = await command.ExecuteScalarAsync();
            return TipoUsuarioExtensions.FromDatabaseValue(tipoUsuario?.ToString());
        }

        public async Task<AgendamentoModel> AtualizarStatusAsync(
            int agendamentoId,
            int usuarioId,
            string status)
        {
            if (agendamentoId <= 0)
                throw new ArgumentException("Agendamento inválido.");

            if (usuarioId <= 0)
                throw new ArgumentException("Usuário inválido.");

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var tipoSolicitante = await ObterTipoUsuario(
                    connection,
                    transaction,
                    usuarioId,
                    "Usuário não encontrado.");

                AgendamentoModel agendamento;
                int responsavelId;
                string statusAtual;

                await using (var command = new NpgsqlCommand(@"
                    SELECT
                        A.ID_AGENDAMENTO,
                        A.CLIENTE_FK,
                        C.USUARIO_FK AS CLIENTE_USUARIO_FK,
                        A.PRESTADOR_FK,
                        A.SERVICO_FK,
                        A.DATA_AGENDAMENTO,
                        A.STATUS_AGENDAMENTO,
                        S.VALOR_SERVICO,
                        S.NOME_SERVICO,
                        S.USUARIO_FK AS RESPONSAVEL_FK,
                        PU.TIPO_USUARIO,
                        PU.NOME_USUARIO AS PROFISSIONAL_NOME,
                        CU.NOME_USUARIO AS CLIENTE_NOME
                    FROM AGENDAMENTOS A
                    INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
                    LEFT JOIN CLIENTES C ON C.ID_CLIENTE = A.CLIENTE_FK
                    LEFT JOIN USUARIOS CU ON CU.ID_USUARIO = C.USUARIO_FK
                    INNER JOIN USUARIOS PU ON PU.ID_USUARIO = A.PRESTADOR_FK
                    WHERE A.ID_AGENDAMENTO = @AgendamentoId
                    FOR UPDATE OF A", connection, transaction))
                {
                    command.Parameters.AddWithValue("@AgendamentoId", agendamentoId);

                    await using var reader = await command.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                        throw new ArgumentException("Agendamento não encontrado.");

                    var prestadorId = reader.GetInt32(reader.GetOrdinal("PRESTADOR_FK"));
                    responsavelId = reader.GetInt32(reader.GetOrdinal("RESPONSAVEL_FK"));
                    var tipoPrestador = reader.GetString(reader.GetOrdinal("TIPO_USUARIO")).ToUpperInvariant();
                    var clienteUsuarioOrdinal = reader.GetOrdinal("CLIENTE_USUARIO_FK");
                    var clienteNomeOrdinal = reader.GetOrdinal("CLIENTE_NOME");
                    var valorServico = reader.GetDecimal(reader.GetOrdinal("VALOR_SERVICO"));
                    statusAtual = reader.GetString(reader.GetOrdinal("STATUS_AGENDAMENTO"));

                    agendamento = new AgendamentoModel
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("ID_AGENDAMENTO")),
                        ClienteId = reader.IsDBNull(clienteUsuarioOrdinal)
                            ? reader.GetInt32(reader.GetOrdinal("CLIENTE_FK"))
                            : reader.GetInt32(clienteUsuarioOrdinal),
                        ProfissionalId = tipoPrestador is "PROFISSIONAL" or "AUTONOMO"
                            ? prestadorId
                            : null,
                        EmpresaId = responsavelId != prestadorId ? responsavelId : null,
                        ServicoId = reader.GetInt32(reader.GetOrdinal("SERVICO_FK")),
                        DataAgendamento = reader.GetDateTime(reader.GetOrdinal("DATA_AGENDAMENTO")),
                        Valor = valorServico,
                        Taxa = 0,
                        ValorServico = valorServico,
                        Status = statusAtual,
                        ClienteNome = reader.IsDBNull(clienteNomeOrdinal)
                            ? string.Empty
                            : reader.GetString(clienteNomeOrdinal),
                        ServicoNome = reader.GetString(reader.GetOrdinal("NOME_SERVICO")),
                        ProfissionalNome = reader.GetString(reader.GetOrdinal("PROFISSIONAL_NOME"))
                    };
                }

                var possuiAcesso = tipoSolicitante switch
                {
                    TipoUsuario.CLIENTE => agendamento.ClienteId == usuarioId,
                    TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO => responsavelId == usuarioId,
                    _ => false
                };

                if (!possuiAcesso)
                    throw new UnauthorizedAccessException();

                var statusDestino = AgendamentoStatusPolicy.ValidarTransicao(
                    statusAtual,
                    status,
                    tipoSolicitante,
                    agendamento.DataAgendamento,
                    ObterAgoraSaoPaulo());

                if (!string.Equals(statusAtual, statusDestino, StringComparison.OrdinalIgnoreCase))
                {
                    await using var updateCommand = new NpgsqlCommand(@"
                        UPDATE AGENDAMENTOS
                        SET STATUS_AGENDAMENTO = @Status
                        WHERE ID_AGENDAMENTO = @AgendamentoId", connection, transaction);

                    updateCommand.Parameters.AddWithValue("@Status", statusDestino);
                    updateCommand.Parameters.AddWithValue("@AgendamentoId", agendamentoId);
                    await updateCommand.ExecuteNonQueryAsync();
                }

                agendamento.Status = statusDestino;
                await transaction.CommitAsync();
                return agendamento;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<int> ConcluirAgendamentosDoDiaAsync(int usuarioId)
        {
            if (usuarioId <= 0)
                throw new ArgumentException("Usuário inválido.");

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var tipoUsuario = await ObterTipoUsuario(
                    connection,
                    transaction,
                    usuarioId,
                    "Usuário não encontrado.");

                if (tipoUsuario is not (TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO))
                    throw new UnauthorizedAccessException();

                var inicio = DateTime.SpecifyKind(ObterAgoraSaoPaulo().Date, DateTimeKind.Unspecified);
                var fim = inicio.AddDays(1);

                await using var command = new NpgsqlCommand(@"
                    UPDATE AGENDAMENTOS A
                    SET STATUS_AGENDAMENTO = 'CONCLUIDO'
                    FROM SERVICOS S
                    WHERE A.SERVICO_FK = S.ID_SERVICO
                      AND S.USUARIO_FK = @UsuarioId
                      AND A.STATUS_AGENDAMENTO = 'AGENDADO'
                      AND A.DATA_AGENDAMENTO >= @Inicio
                      AND A.DATA_AGENDAMENTO < @Fim", connection, transaction);

                command.Parameters.AddWithValue("@UsuarioId", usuarioId);
                command.Parameters.Add("@Inicio", NpgsqlDbType.Timestamp).Value = inicio;
                command.Parameters.Add("@Fim", NpgsqlDbType.Timestamp).Value = fim;

                var quantidade = await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
                return quantidade;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private async Task<List<AgendamentoModel>> ListarAgendamentos(
            string sql,
            Action<NpgsqlCommand> configureCommand)
        {
            var lista = new List<AgendamentoModel>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand(sql, connection);
            configureCommand(command);

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var prestadorId = reader.GetInt32(reader.GetOrdinal("PRESTADOR_FK"));
                var responsavelId = reader.GetInt32(reader.GetOrdinal("RESPONSAVEL_FK"));
                var tipoUsuario = reader.GetString(reader.GetOrdinal("TIPO_USUARIO")).ToUpperInvariant();
                var valorServico = reader.GetDecimal(reader.GetOrdinal("VALOR_SERVICO"));
                var clienteUsuarioOrdinal = reader.GetOrdinal("CLIENTE_USUARIO_FK");
                var clienteNomeOrdinal = reader.GetOrdinal("CLIENTE_NOME");

                lista.Add(new AgendamentoModel
                {
                    Id = reader.GetInt32(reader.GetOrdinal("ID_AGENDAMENTO")),
                    ClienteId = reader.IsDBNull(clienteUsuarioOrdinal)
                        ? reader.GetInt32(reader.GetOrdinal("CLIENTE_FK"))
                        : reader.GetInt32(clienteUsuarioOrdinal),
                    ProfissionalId = tipoUsuario is "PROFISSIONAL" or "AUTONOMO" ? prestadorId : null,
                    EmpresaId = responsavelId != prestadorId ? responsavelId : null,
                    ServicoId = reader.GetInt32(reader.GetOrdinal("SERVICO_FK")),
                    DataAgendamento = reader.GetDateTime(reader.GetOrdinal("DATA_AGENDAMENTO")),
                    Valor = valorServico,
                    Taxa = 0,
                    ValorServico = valorServico,
                    Status = reader.GetString(reader.GetOrdinal("STATUS_AGENDAMENTO")),
                    ClienteNome = reader.IsDBNull(clienteNomeOrdinal)
                        ? string.Empty
                        : reader.GetString(clienteNomeOrdinal),
                    ServicoNome = reader.GetString(reader.GetOrdinal("NOME_SERVICO")),
                    ProfissionalNome = reader.GetString(reader.GetOrdinal("PROFISSIONAL_NOME"))
                });
            }

            return lista;
        }

        private static void ValidarDadosBasicos(AgendamentoModel agendamento)
        {
            if (agendamento.ClienteId <= 0)
                throw new ArgumentException("Cliente invalido.");

            if (agendamento.ServicoId <= 0)
                throw new ArgumentException("Servico invalido.");

            if (agendamento.DataAgendamento == default)
                throw new ArgumentException("Data e horario do agendamento sao obrigatorios.");

            if (!agendamento.ProfissionalId.HasValue || agendamento.ProfissionalId.Value <= 0)
                throw new ArgumentException("Prestador ou profissional invalido.");

            if (agendamento.EmpresaId.HasValue && agendamento.EmpresaId.Value <= 0)
                throw new ArgumentException("Empresa invalida.");
        }

        private static int ObterResponsavelId(AgendamentoModel agendamento)
        {
            return agendamento.EmpresaId ?? agendamento.ProfissionalId!.Value;
        }

        private static int ObterPrestadorId(AgendamentoModel agendamento)
        {
            return agendamento.ProfissionalId!.Value;
        }

        private static async Task<int> ObterClienteFk(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int clienteId)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT ID_CLIENTE
                FROM CLIENTES
                WHERE USUARIO_FK = @ClienteId OR ID_CLIENTE = @ClienteId
                ORDER BY CASE WHEN USUARIO_FK = @ClienteId THEN 0 ELSE 1 END
                LIMIT 1", connection, transaction);

            command.Parameters.AddWithValue("@ClienteId", clienteId);

            var result = await command.ExecuteScalarAsync();
            if (result == null)
                throw new ArgumentException("Cliente nao encontrado.");

            return Convert.ToInt32(result);
        }

        private static async Task<ServicoAgendamentoConfig> ObterServico(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int servicoId,
            int responsavelId)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT
                    VALOR_SERVICO,
                    TEMPO_ESTIMADO_MINUTOS
                FROM SERVICOS
                WHERE ID_SERVICO = @ServicoId
                  AND USUARIO_FK = @ResponsavelId", connection, transaction);

            command.Parameters.AddWithValue("@ServicoId", servicoId);
            command.Parameters.AddWithValue("@ResponsavelId", responsavelId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new ArgumentException("Servico nao encontrado para o responsavel informado.");

            var duracao = reader.GetInt32(reader.GetOrdinal("TEMPO_ESTIMADO_MINUTOS"));
            if (duracao <= 0)
                throw new ArgumentException("O servico precisa ter uma duracao valida.");

            return new ServicoAgendamentoConfig
            {
                Valor = reader.GetDecimal(reader.GetOrdinal("VALOR_SERVICO")),
                DuracaoMinutos = duracao
            };
        }

        private static async Task ValidarResponsavelEPrestador(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            AgendamentoModel agendamento,
            int responsavelId,
            int prestadorId)
        {
            var tipoResponsavel = await ObterTipoUsuario(
                connection,
                transaction,
                responsavelId,
                "Responsavel pelo agendamento nao encontrado.");

            if (!agendamento.EmpresaId.HasValue)
            {
                if (tipoResponsavel != TipoUsuario.AUTONOMO || responsavelId != prestadorId)
                    throw new ArgumentException("O responsavel informado nao e um autonomo valido.");

                return;
            }

            if (tipoResponsavel != TipoUsuario.EMPRESA)
                throw new ArgumentException("A empresa informada e invalida.");

            var tipoPrestador = await ObterTipoUsuario(
                connection,
                transaction,
                prestadorId,
                "Profissional nao encontrado.");

            if (tipoPrestador != TipoUsuario.PROFISSIONAL)
                throw new ArgumentException("Selecione um usuario com papel PROFISSIONAL.");

            await using (var vinculoCommand = new NpgsqlCommand(@"
                SELECT STATUS_SOLICITACAO
                FROM VINCULOS
                WHERE PROFISSIONAL_FK = @ProfissionalId
                  AND EMPRESA_FK = @EmpresaId
                ORDER BY DATA_SOLICITACAO DESC, ID_SOLICITACAO DESC
                LIMIT 1", connection, transaction))
            {
                vinculoCommand.Parameters.AddWithValue("@ProfissionalId", prestadorId);
                vinculoCommand.Parameters.AddWithValue("@EmpresaId", responsavelId);

                var statusVinculo = (await vinculoCommand.ExecuteScalarAsync())?.ToString();
                if (!string.Equals(statusVinculo, "APROVADO", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("O profissional nao possui vinculo ativo com a empresa.");
            }

            await using var servicoCommand = new NpgsqlCommand(@"
                SELECT 1
                FROM PROFISSIONAL_SERVICOS
                WHERE PROFISSIONAL_FK = @ProfissionalId
                  AND EMPRESA_FK = @EmpresaId
                  AND SERVICO_FK = @ServicoId
                LIMIT 1", connection, transaction);

            servicoCommand.Parameters.AddWithValue("@ProfissionalId", prestadorId);
            servicoCommand.Parameters.AddWithValue("@EmpresaId", responsavelId);
            servicoCommand.Parameters.AddWithValue("@ServicoId", agendamento.ServicoId);

            if (await servicoCommand.ExecuteScalarAsync() == null)
                throw new ArgumentException("O profissional selecionado nao realiza o servico informado.");
        }

        private static async Task<TipoUsuario> ObterTipoUsuario(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int usuarioId,
            string mensagemNaoEncontrado)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT TIPO_USUARIO
                FROM USUARIOS
                WHERE ID_USUARIO = @UsuarioId", connection, transaction);

            command.Parameters.AddWithValue("@UsuarioId", usuarioId);

            var result = await command.ExecuteScalarAsync();
            if (result == null)
                throw new ArgumentException(mensagemNaoEncontrado);

            return TipoUsuarioExtensions.FromDatabaseValue(result.ToString());
        }

        private static async Task AdquirirBloqueioPrestador(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int prestadorId)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(2, @PrestadorId)",
                connection,
                transaction);

            command.Parameters.AddWithValue("@PrestadorId", prestadorId);
            await command.ExecuteScalarAsync();
        }

        private static async Task AdquirirBloqueioCliente(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int clienteFk)
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(1, @ClienteFk)",
                connection,
                transaction);

            command.Parameters.AddWithValue("@ClienteFk", clienteFk);
            await command.ExecuteScalarAsync();
        }

        private static async Task<bool> ExisteAgendamentoAtivoClienteNaData(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int clienteFk,
            DateTime data)
        {
            var inicio = DateTime.SpecifyKind(data.Date, DateTimeKind.Unspecified);
            var fim = inicio.AddDays(1);

            await using var command = new NpgsqlCommand(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM AGENDAMENTOS
                    WHERE CLIENTE_FK = @ClienteFk
                      AND DATA_AGENDAMENTO >= @Inicio
                      AND DATA_AGENDAMENTO < @Fim
                      AND UPPER(TRIM(STATUS_AGENDAMENTO)) NOT IN ('CONCLUIDO', 'CANCELADO')
                )", connection, transaction);

            command.Parameters.AddWithValue("@ClienteFk", clienteFk);
            command.Parameters.Add("@Inicio", NpgsqlDbType.Timestamp).Value = inicio;
            command.Parameters.Add("@Fim", NpgsqlDbType.Timestamp).Value = fim;

            return Convert.ToBoolean(await command.ExecuteScalarAsync());
        }

        private static async Task<FuncionamentoPrestadorConfig> ObterFuncionamentoResponsavel(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int responsavelId)
        {
            var config = FuncionamentoPrestadorConfig.Default();

            await using var command = new NpgsqlCommand(@"
                SELECT
                    DIA_FUNCIONAMENTO,
                    HORA_INICIO,
                    HORA_FIM
                FROM FUNCIONAMENTO_INTERVALOS
                WHERE USUARIO_FK = @ResponsavelId
                ORDER BY DIA_FUNCIONAMENTO, HORA_INICIO", connection, transaction);

            command.Parameters.AddWithValue("@ResponsavelId", responsavelId);

            var intervalos = new List<IntervaloFuncionamentoConfig>();
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                intervalos.Add(new IntervaloFuncionamentoConfig
                {
                    DiaFuncionamento = DiaFuncionamentoExtensions.FromDatabaseValue(
                        reader.GetString(reader.GetOrdinal("DIA_FUNCIONAMENTO"))),
                    HoraInicio = reader.GetTimeSpan(reader.GetOrdinal("HORA_INICIO")),
                    HoraFim = reader.GetTimeSpan(reader.GetOrdinal("HORA_FIM"))
                });
            }

            if (intervalos.Count > 0)
                config.Intervalos = intervalos;

            return config;
        }

        private static IntervaloFuncionamentoConfig? ObterIntervaloFuncionamento(
            FuncionamentoPrestadorConfig funcionamento,
            DateTime inicio,
            int duracaoMinutos)
        {
            var diaFuncionamento = DiaFuncionamentoExtensions.FromDayOfWeek(inicio.DayOfWeek);
            var horaInicio = inicio.TimeOfDay;
            var horaFim = horaInicio.Add(TimeSpan.FromMinutes(duracaoMinutos));

            return funcionamento.Intervalos
                .Where(intervalo => intervalo.DiaFuncionamento == diaFuncionamento)
                .OrderBy(intervalo => intervalo.HoraInicio)
                .FirstOrDefault(intervalo =>
                    horaInicio >= intervalo.HoraInicio &&
                    horaFim <= intervalo.HoraFim);
        }

        private static async Task<bool> ExisteConflito(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int prestadorId,
            DateTime inicio,
            DateTime fim)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT EXISTS (
                    SELECT 1
                    FROM AGENDAMENTOS A
                    INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
                    WHERE A.PRESTADOR_FK = @PrestadorId
                      AND A.STATUS_AGENDAMENTO <> 'CANCELADO'
                      AND A.DATA_AGENDAMENTO < @Fim
                      AND A.DATA_AGENDAMENTO
                            + (GREATEST(S.TEMPO_ESTIMADO_MINUTOS, 1) * INTERVAL '1 minute') > @Inicio
                )", connection, transaction);

            command.Parameters.AddWithValue("@PrestadorId", prestadorId);
            command.Parameters.Add("@Inicio", NpgsqlDbType.Timestamp).Value =
                DateTime.SpecifyKind(inicio, DateTimeKind.Unspecified);
            command.Parameters.Add("@Fim", NpgsqlDbType.Timestamp).Value =
                DateTime.SpecifyKind(fim, DateTimeKind.Unspecified);

            return Convert.ToBoolean(await command.ExecuteScalarAsync());
        }

        private static async Task<AgendamentoIndisponivelException> CriarIndisponibilidade(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int prestadorId,
            FuncionamentoPrestadorConfig funcionamento,
            int duracaoMinutos,
            DateTime inicioSolicitado,
            string mensagem)
        {
            var sugestao = await ObterProximoHorarioDisponivel(
                connection,
                transaction,
                prestadorId,
                funcionamento,
                duracaoMinutos,
                inicioSolicitado);

            return new AgendamentoIndisponivelException(mensagem, sugestao);
        }

        private static async Task<DateTime?> ObterProximoHorarioDisponivel(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int prestadorId,
            FuncionamentoPrestadorConfig funcionamento,
            int duracaoMinutos,
            DateTime inicioSolicitado)
        {
            var agora = ObterAgoraSaoPaulo();
            var inicioBusca = inicioSolicitado > agora ? inicioSolicitado : agora;
            var inicioPeriodo = inicioBusca.Date;
            var fimPeriodo = inicioPeriodo.AddDays(DiasBuscaSugestao + 1);

            var ocupados = await ObterIntervalosOcupados(
                connection,
                transaction,
                prestadorId,
                inicioPeriodo,
                fimPeriodo);

            for (var dia = 0; dia <= DiasBuscaSugestao; dia++)
            {
                var data = inicioPeriodo.AddDays(dia);
                var diaFuncionamento = DiaFuncionamentoExtensions.FromDayOfWeek(data.DayOfWeek);
                var intervalos = funcionamento.Intervalos
                    .Where(intervalo => intervalo.DiaFuncionamento == diaFuncionamento)
                    .OrderBy(intervalo => intervalo.HoraInicio);

                foreach (var intervalo in intervalos)
                {
                    var inicioIntervalo = data.Add(intervalo.HoraInicio);
                    var fimIntervalo = data.Add(intervalo.HoraFim);
                    var candidato = inicioIntervalo;

                    if (data == inicioBusca.Date && inicioBusca > candidato)
                        candidato = inicioBusca;

                    candidato = ArredondarParaIntervalo(candidato);

                    while (candidato.AddMinutes(duracaoMinutos) <= fimIntervalo)
                    {
                        var fimCandidato = candidato.AddMinutes(duracaoMinutos);
                        var conflito = ocupados
                            .Where(ocupado =>
                                ocupado.Inicio < fimCandidato &&
                                ocupado.Fim > candidato)
                            .OrderBy(ocupado => ocupado.Fim)
                            .FirstOrDefault();

                        if (conflito == null)
                            return DateTime.SpecifyKind(candidato, DateTimeKind.Unspecified);

                        candidato = ArredondarParaIntervalo(
                            conflito.Fim > candidato
                                ? conflito.Fim
                                : candidato.AddMinutes(IntervaloSugestaoMinutos));
                    }
                }
            }

            return null;
        }

        private static async Task<List<IntervaloOcupado>> ObterIntervalosOcupados(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int prestadorId,
            DateTime inicio,
            DateTime fim)
        {
            var intervalos = new List<IntervaloOcupado>();

            await using var command = new NpgsqlCommand(@"
                SELECT
                    A.DATA_AGENDAMENTO,
                    GREATEST(S.TEMPO_ESTIMADO_MINUTOS, 1) AS DURACAO_MINUTOS
                FROM AGENDAMENTOS A
                INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
                WHERE A.PRESTADOR_FK = @PrestadorId
                  AND A.STATUS_AGENDAMENTO <> 'CANCELADO'
                  AND A.DATA_AGENDAMENTO >= @Inicio
                  AND A.DATA_AGENDAMENTO < @Fim
                ORDER BY A.DATA_AGENDAMENTO", connection, transaction);

            command.Parameters.AddWithValue("@PrestadorId", prestadorId);
            command.Parameters.Add("@Inicio", NpgsqlDbType.Timestamp).Value =
                DateTime.SpecifyKind(inicio, DateTimeKind.Unspecified);
            command.Parameters.Add("@Fim", NpgsqlDbType.Timestamp).Value =
                DateTime.SpecifyKind(fim, DateTimeKind.Unspecified);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var inicioOcupado = reader.GetDateTime(reader.GetOrdinal("DATA_AGENDAMENTO"));
                var duracao = reader.GetInt32(reader.GetOrdinal("DURACAO_MINUTOS"));

                intervalos.Add(new IntervaloOcupado
                {
                    Inicio = inicioOcupado,
                    Fim = inicioOcupado.AddMinutes(duracao)
                });
            }

            return intervalos;
        }

        private static async Task<List<DateTime>> ObterHorariosDisponiveisNaData(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int prestadorId,
            FuncionamentoPrestadorConfig funcionamento,
            int duracaoMinutos,
            DateTime data)
        {
            var horarios = new List<DateTime>();
            var inicioDia = data.Date;
            var fimDia = inicioDia.AddDays(1);
            var agora = ObterAgoraSaoPaulo();

            if (inicioDia < agora.Date)
                return horarios;

            var ocupados = await ObterIntervalosOcupados(
                connection,
                transaction,
                prestadorId,
                inicioDia,
                fimDia);

            var diaFuncionamento = DiaFuncionamentoExtensions.FromDayOfWeek(inicioDia.DayOfWeek);
            var intervalos = funcionamento.Intervalos
                .Where(intervalo => intervalo.DiaFuncionamento == diaFuncionamento)
                .OrderBy(intervalo => intervalo.HoraInicio);

            foreach (var intervalo in intervalos)
            {
                var candidato = inicioDia.Add(intervalo.HoraInicio);
                var fimIntervalo = inicioDia.Add(intervalo.HoraFim);

                if (inicioDia == agora.Date && candidato <= agora)
                    candidato = ArredondarParaIntervalo(agora.AddTicks(1));

                while (candidato.AddMinutes(duracaoMinutos) <= fimIntervalo)
                {
                    var fimCandidato = candidato.AddMinutes(duracaoMinutos);
                    var possuiConflito = ocupados.Any(ocupado =>
                        ocupado.Inicio < fimCandidato &&
                        ocupado.Fim > candidato);

                    if (!possuiConflito)
                        horarios.Add(DateTime.SpecifyKind(candidato, DateTimeKind.Unspecified));

                    candidato = candidato.AddMinutes(IntervaloSugestaoMinutos);
                }
            }

            return horarios;
        }

        private static DateTime ArredondarParaIntervalo(DateTime data)
        {
            var intervaloTicks = TimeSpan.FromMinutes(IntervaloSugestaoMinutos).Ticks;
            var resto = data.Ticks % intervaloTicks;
            var ticks = resto == 0
                ? data.Ticks
                : data.Ticks + intervaloTicks - resto;

            return DateTime.SpecifyKind(new DateTime(ticks), DateTimeKind.Unspecified);
        }

        private static string NormalizarStatus(string? status)
        {
            var statusNormalizado = status?.ToUpperInvariant();
            var statusValido = new[] { "AGENDADO", "CONCLUIDO", "CANCELADO" };

            return statusValido.Contains(statusNormalizado) ? statusNormalizado! : "AGENDADO";
        }

        private static DateTime NormalizarDataAgendamento(DateTime dataAgendamento)
        {
            var timeZone = ObterTimeZoneSaoPaulo();

            var dataLocal = dataAgendamento.Kind switch
            {
                DateTimeKind.Utc => TimeZoneInfo.ConvertTimeFromUtc(dataAgendamento, timeZone),
                DateTimeKind.Local => TimeZoneInfo.ConvertTime(dataAgendamento, timeZone),
                _ => dataAgendamento
            };

            return DateTime.SpecifyKind(dataLocal, DateTimeKind.Unspecified);
        }

        private static DateTime ObterAgoraSaoPaulo()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ObterTimeZoneSaoPaulo());
        }

        private static TimeZoneInfo ObterTimeZoneSaoPaulo()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
            }
        }

        private sealed class ServicoAgendamentoConfig
        {
            public decimal Valor { get; init; }
            public int DuracaoMinutos { get; init; }
        }

        private sealed class IntervaloOcupado
        {
            public DateTime Inicio { get; init; }
            public DateTime Fim { get; init; }
        }

        private sealed class FuncionamentoPrestadorConfig
        {
            public List<IntervaloFuncionamentoConfig> Intervalos { get; set; } = IntervalosPadrao();

            public static FuncionamentoPrestadorConfig Default()
            {
                return new FuncionamentoPrestadorConfig();
            }

            private static List<IntervaloFuncionamentoConfig> IntervalosPadrao()
            {
                return new List<IntervaloFuncionamentoConfig>
                {
                    new() { DiaFuncionamento = DiaFuncionamento.SEGUNDA, HoraInicio = new TimeSpan(8, 0, 0), HoraFim = new TimeSpan(18, 0, 0) },
                    new() { DiaFuncionamento = DiaFuncionamento.TERCA, HoraInicio = new TimeSpan(8, 0, 0), HoraFim = new TimeSpan(18, 0, 0) },
                    new() { DiaFuncionamento = DiaFuncionamento.QUARTA, HoraInicio = new TimeSpan(8, 0, 0), HoraFim = new TimeSpan(18, 0, 0) },
                    new() { DiaFuncionamento = DiaFuncionamento.QUINTA, HoraInicio = new TimeSpan(8, 0, 0), HoraFim = new TimeSpan(18, 0, 0) },
                    new() { DiaFuncionamento = DiaFuncionamento.SEXTA, HoraInicio = new TimeSpan(8, 0, 0), HoraFim = new TimeSpan(18, 0, 0) }
                };
            }
        }

        private sealed class IntervaloFuncionamentoConfig
        {
            public DiaFuncionamento DiaFuncionamento { get; init; }
            public TimeSpan HoraInicio { get; init; }
            public TimeSpan HoraFim { get; init; }
        }
    }
}
