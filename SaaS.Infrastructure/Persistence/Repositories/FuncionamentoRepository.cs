using System.Globalization;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class FuncionamentoRepository : IFuncionamentoRepository
    {
        private readonly string _connectionString;

        public FuncionamentoRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<FuncionamentoConfigModel?> ObterFuncionamento(int usuarioId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await ValidarUsuarioPodeConfigurar(connection, usuarioId);

            var funcionamento = await ObterConfigBase(connection, usuarioId);
            funcionamento.Intervalos = await ObterIntervalos(connection, usuarioId);
            AplicarResumoLegado(funcionamento);

            return funcionamento;
        }

        public async Task<FuncionamentoConfigModel> SalvarFuncionamento(FuncionamentoConfigModel funcionamento)
        {
            if (funcionamento.UsuarioFk <= 0)
                throw new ArgumentException("Informe um usuario valido para configurar o funcionamento.");

            var limiteDiario = NormalizarLimite(funcionamento.LimiteDiario);
            var limiteSemanal = NormalizarLimite(funcionamento.LimiteSemanal);
            var intervalos = NormalizarIntervalos(funcionamento);

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await ValidarUsuarioPodeConfigurar(connection, funcionamento.UsuarioFk, transaction);
                await SalvarConfigBase(connection, transaction, funcionamento.UsuarioFk, limiteDiario, limiteSemanal);
                await SubstituirIntervalos(connection, transaction, funcionamento.UsuarioFk, intervalos);

                await transaction.CommitAsync();

                funcionamento.LimiteDiario = limiteDiario;
                funcionamento.LimiteSemanal = limiteSemanal;
                funcionamento.Intervalos = intervalos;
                AplicarResumoLegado(funcionamento);

                return funcionamento;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        private static async Task<FuncionamentoConfigModel> ObterConfigBase(NpgsqlConnection connection, int usuarioId)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT
                    USUARIO_FK,
                    LIMITE_DIARIO,
                    LIMITE_SEMANAL
                FROM PRESTADOR_CONFIG
                WHERE USUARIO_FK = @UsuarioFk
                ORDER BY ID_PRESTADOR_CONFIG DESC
                LIMIT 1", connection);

            command.Parameters.AddWithValue("@UsuarioFk", usuarioId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return CriarFuncionamentoPadrao(usuarioId);

            return new FuncionamentoConfigModel
            {
                UsuarioFk = reader.GetInt32(reader.GetOrdinal("USUARIO_FK")),
                LimiteDiario = reader.GetInt32(reader.GetOrdinal("LIMITE_DIARIO")),
                LimiteSemanal = reader.GetInt32(reader.GetOrdinal("LIMITE_SEMANAL"))
            };
        }

        private static async Task<List<FuncionamentoIntervaloModel>> ObterIntervalos(NpgsqlConnection connection, int usuarioId)
        {
            var intervalos = new List<FuncionamentoIntervaloModel>();

            await using var command = new NpgsqlCommand(@"
                SELECT
                    DIA_FUNCIONAMENTO,
                    HORA_INICIO,
                    HORA_FIM
                FROM FUNCIONAMENTO_INTERVALOS
                WHERE USUARIO_FK = @UsuarioFk
                ORDER BY DIA_FUNCIONAMENTO, HORA_INICIO", connection);

            command.Parameters.AddWithValue("@UsuarioFk", usuarioId);

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var horaInicio = FormatarHorario(reader.GetTimeSpan(reader.GetOrdinal("HORA_INICIO")));
                var horaFim = FormatarHorario(reader.GetTimeSpan(reader.GetOrdinal("HORA_FIM")));

                intervalos.Add(new FuncionamentoIntervaloModel
                {
                    DiaFuncionamento = DiaFuncionamentoExtensions.FromDatabaseValue(reader.GetString(reader.GetOrdinal("DIA_FUNCIONAMENTO"))),
                    HoraInicio = horaInicio,
                    HoraFim = horaFim,
                    HoraEntrada = horaInicio,
                    HoraSaida = horaFim
                });
            }

            return intervalos.Count == 0 ? CriarIntervalosPadrao() : intervalos;
        }

        private static async Task SalvarConfigBase(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int usuarioId,
            int limiteDiario,
            int limiteSemanal)
        {
            await using var updateCommand = new NpgsqlCommand(@"
                UPDATE PRESTADOR_CONFIG
                SET LIMITE_DIARIO = @LimiteDiario,
                    LIMITE_SEMANAL = @LimiteSemanal
                WHERE USUARIO_FK = @UsuarioFk", connection, transaction);

            updateCommand.Parameters.AddWithValue("@UsuarioFk", usuarioId);
            updateCommand.Parameters.AddWithValue("@LimiteDiario", limiteDiario);
            updateCommand.Parameters.AddWithValue("@LimiteSemanal", limiteSemanal);

            var linhasAtualizadas = await updateCommand.ExecuteNonQueryAsync();
            if (linhasAtualizadas > 0)
                return;

            await using var insertCommand = new NpgsqlCommand(@"
                INSERT INTO PRESTADOR_CONFIG (USUARIO_FK, LIMITE_DIARIO, LIMITE_SEMANAL)
                VALUES (@UsuarioFk, @LimiteDiario, @LimiteSemanal)", connection, transaction);

            insertCommand.Parameters.AddWithValue("@UsuarioFk", usuarioId);
            insertCommand.Parameters.AddWithValue("@LimiteDiario", limiteDiario);
            insertCommand.Parameters.AddWithValue("@LimiteSemanal", limiteSemanal);

            await insertCommand.ExecuteNonQueryAsync();
        }

        private static async Task SubstituirIntervalos(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int usuarioId,
            List<FuncionamentoIntervaloModel> intervalos)
        {
            await using var deleteCommand = new NpgsqlCommand(@"
                DELETE FROM FUNCIONAMENTO_INTERVALOS
                WHERE USUARIO_FK = @UsuarioFk", connection, transaction);

            deleteCommand.Parameters.AddWithValue("@UsuarioFk", usuarioId);
            await deleteCommand.ExecuteNonQueryAsync();

            foreach (var intervalo in intervalos)
            {
                await using var insertCommand = new NpgsqlCommand(@"
                    INSERT INTO FUNCIONAMENTO_INTERVALOS (
                        USUARIO_FK,
                        DIA_FUNCIONAMENTO,
                        HORA_INICIO,
                        HORA_FIM
                    )
                    VALUES (
                        @UsuarioFk,
                        @DiaFuncionamento,
                        @HoraInicio,
                        @HoraFim
                    )", connection, transaction);

                insertCommand.Parameters.AddWithValue("@UsuarioFk", usuarioId);
                insertCommand.Parameters.AddWithValue("@DiaFuncionamento", intervalo.DiaFuncionamento.ToDatabaseValue());
                insertCommand.Parameters.Add("@HoraInicio", NpgsqlDbType.Time).Value = ObterHoraInicio(intervalo);
                insertCommand.Parameters.Add("@HoraFim", NpgsqlDbType.Time).Value = ObterHoraFim(intervalo);

                await insertCommand.ExecuteNonQueryAsync();
            }
        }

        private static async Task ValidarUsuarioPodeConfigurar(
            NpgsqlConnection connection,
            int usuarioId,
            NpgsqlTransaction? transaction = null)
        {
            await using var command = new NpgsqlCommand(@"
                SELECT TIPO_USUARIO
                FROM USUARIOS
                WHERE ID_USUARIO = @UsuarioFk", connection, transaction);

            command.Parameters.AddWithValue("@UsuarioFk", usuarioId);

            var result = await command.ExecuteScalarAsync();
            if (result == null)
                throw new ArgumentException("Usuario nao encontrado.");

            var tipoUsuario = TipoUsuarioExtensions.FromDatabaseValue(result.ToString());
            if (tipoUsuario is not (TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO))
                throw new ArgumentException("Apenas usuarios EMPRESA ou AUTONOMO podem configurar funcionamento.");
        }

        private static FuncionamentoConfigModel CriarFuncionamentoPadrao(int usuarioId)
        {
            var funcionamento = new FuncionamentoConfigModel
            {
                UsuarioFk = usuarioId,
                LimiteDiario = 1,
                LimiteSemanal = 1,
                Intervalos = CriarIntervalosPadrao()
            };

            AplicarResumoLegado(funcionamento);
            return funcionamento;
        }

        private static List<FuncionamentoIntervaloModel> CriarIntervalosPadrao()
        {
            return new List<FuncionamentoIntervaloModel>
            {
                CriarIntervalo(DiaFuncionamento.SEGUNDA, "08:00", "18:00"),
                CriarIntervalo(DiaFuncionamento.TERCA, "08:00", "18:00"),
                CriarIntervalo(DiaFuncionamento.QUARTA, "08:00", "18:00"),
                CriarIntervalo(DiaFuncionamento.QUINTA, "08:00", "18:00"),
                CriarIntervalo(DiaFuncionamento.SEXTA, "08:00", "18:00")
            };
        }

        private static FuncionamentoIntervaloModel CriarIntervalo(DiaFuncionamento dia, string horaInicio, string horaFim)
        {
            return new FuncionamentoIntervaloModel
            {
                DiaFuncionamento = dia,
                HoraInicio = horaInicio,
                HoraFim = horaFim,
                HoraEntrada = horaInicio,
                HoraSaida = horaFim
            };
        }

        private static List<FuncionamentoIntervaloModel> NormalizarIntervalos(FuncionamentoConfigModel funcionamento)
        {
            var intervalosInformados = funcionamento.Intervalos?
                .Where(intervalo => intervalo != null)
                .ToList() ?? new List<FuncionamentoIntervaloModel>();

            if (intervalosInformados.Count == 0)
            {
                intervalosInformados = CriarIntervalosLegados(funcionamento);
            }

            if (intervalosInformados.Count == 0)
                throw new ArgumentException("Informe ao menos um intervalo de funcionamento.");

            var intervalos = intervalosInformados
                .Select(intervalo =>
                {
                    var inicio = ObterHoraInicio(intervalo);
                    var fim = ObterHoraFim(intervalo);

                    if (inicio >= fim)
                        throw new ArgumentException("Hora de entrada deve ser menor que hora de saida.");

                    return CriarIntervalo(
                        intervalo.DiaFuncionamento,
                        FormatarHorario(inicio),
                        FormatarHorario(fim));
                })
                .OrderBy(intervalo => intervalo.DiaFuncionamento)
                .ThenBy(ObterHoraInicio)
                .ToList();

            ValidarSobreposicao(intervalos);
            return intervalos;
        }

        private static List<FuncionamentoIntervaloModel> CriarIntervalosLegados(FuncionamentoConfigModel funcionamento)
        {
            var dias = funcionamento.DiasFuncionamento?
                .Distinct()
                .OrderBy(dia => dia)
                .ToList() ?? new List<DiaFuncionamento>();

            if (dias.Count == 0)
                return new List<FuncionamentoIntervaloModel>();

            var horaInicio = funcionamento.HoraInicio ?? "08:00";
            var horaFim = funcionamento.HoraFim ?? "18:00";

            return dias
                .Select(dia => CriarIntervalo(dia, horaInicio, horaFim))
                .ToList();
        }

        private static void ValidarSobreposicao(List<FuncionamentoIntervaloModel> intervalos)
        {
            foreach (var grupo in intervalos.GroupBy(intervalo => intervalo.DiaFuncionamento))
            {
                FuncionamentoIntervaloModel? anterior = null;
                foreach (var atual in grupo.OrderBy(ObterHoraInicio))
                {
                    if (anterior != null && ObterHoraInicio(atual) < ObterHoraFim(anterior))
                    {
                        throw new ArgumentException($"Existem intervalos sobrepostos para {grupo.Key}.");
                    }

                    anterior = atual;
                }
            }
        }

        private static void AplicarResumoLegado(FuncionamentoConfigModel funcionamento)
        {
            funcionamento.DiasFuncionamento = funcionamento.Intervalos
                .Select(intervalo => intervalo.DiaFuncionamento)
                .Distinct()
                .OrderBy(dia => dia)
                .ToList();

            var primeiroIntervalo = funcionamento.Intervalos
                .OrderBy(intervalo => intervalo.DiaFuncionamento)
                .ThenBy(ObterHoraInicio)
                .FirstOrDefault();

            funcionamento.HoraInicio = primeiroIntervalo?.HoraInicio ?? "08:00";
            funcionamento.HoraFim = primeiroIntervalo?.HoraFim ?? "18:00";
        }

        private static TimeSpan ObterHoraInicio(FuncionamentoIntervaloModel intervalo)
        {
            return ParseHorario(intervalo.HoraInicio ?? intervalo.HoraEntrada, "HoraInicio");
        }

        private static TimeSpan ObterHoraFim(FuncionamentoIntervaloModel intervalo)
        {
            return ParseHorario(intervalo.HoraFim ?? intervalo.HoraSaida, "HoraFim");
        }

        private static TimeSpan ParseHorario(string? horario, string nomeCampo)
        {
            if (!TimeSpan.TryParse(horario, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < TimeSpan.Zero ||
                parsed >= TimeSpan.FromDays(1))
            {
                throw new ArgumentException($"{nomeCampo} deve estar no formato HH:mm.");
            }

            return parsed;
        }

        private static string FormatarHorario(TimeSpan horario)
        {
            return horario.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }

        private static int NormalizarLimite(int? limite)
        {
            var valor = limite.GetValueOrDefault(1);
            return valor <= 0 ? 1 : valor;
        }
    }
}
