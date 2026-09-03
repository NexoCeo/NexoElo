using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Persistence.Repositories;

public sealed class RelatorioRepository : IRelatorioRepository
{
    private readonly string _connectionString;

    public RelatorioRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("A conexao com o banco nao foi configurada.");
    }

    public async Task<RelatorioMensalModel> ObterRelatorioMensalAsync(
        int usuarioId,
        int ano,
        int mes)
    {
        if (usuarioId <= 0)
            throw new ArgumentException("Usuario invalido.");

        if (ano is < 2000 or > 2100 || mes is < 1 or > 12)
            throw new ArgumentException("Periodo invalido.");

        var inicio = DateTime.SpecifyKind(new DateTime(ano, mes, 1), DateTimeKind.Unspecified);
        var fim = DateTime.SpecifyKind(inicio.AddMonths(1), DateTimeKind.Unspecified);

        const string sql = @"
            SELECT NOME_USUARIO, TIPO_USUARIO
            FROM USUARIOS
            WHERE ID_USUARIO = @UsuarioId;

            SELECT
                COUNT(*) AS TOTAL_AGENDAMENTOS,
                COUNT(*) FILTER (WHERE UPPER(A.STATUS_AGENDAMENTO) = 'AGENDADO') AS TOTAL_AGENDADOS,
                COUNT(*) FILTER (WHERE UPPER(A.STATUS_AGENDAMENTO) = 'CONCLUIDO') AS TOTAL_CONCLUIDOS,
                COUNT(*) FILTER (WHERE UPPER(A.STATUS_AGENDAMENTO) = 'CANCELADO') AS TOTAL_CANCELADOS,
                COALESCE(SUM(S.VALOR_SERVICO) FILTER (
                    WHERE UPPER(A.STATUS_AGENDAMENTO) = 'CONCLUIDO'), 0) AS FATURAMENTO_TOTAL
            FROM AGENDAMENTOS A
            INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
            WHERE S.USUARIO_FK = @UsuarioId
              AND A.DATA_AGENDAMENTO >= @Inicio
              AND A.DATA_AGENDAMENTO < @Fim;

            SELECT
                S.NOME_SERVICO,
                COUNT(*) AS QUANTIDADE_CONCLUIDA,
                COALESCE(SUM(S.VALOR_SERVICO), 0) AS FATURAMENTO
            FROM AGENDAMENTOS A
            INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
            WHERE S.USUARIO_FK = @UsuarioId
              AND A.DATA_AGENDAMENTO >= @Inicio
              AND A.DATA_AGENDAMENTO < @Fim
              AND UPPER(A.STATUS_AGENDAMENTO) = 'CONCLUIDO'
            GROUP BY S.ID_SERVICO, S.NOME_SERVICO
            ORDER BY QUANTIDADE_CONCLUIDA DESC, FATURAMENTO DESC, S.NOME_SERVICO;

            SELECT
                DATE_TRUNC('day', A.DATA_AGENDAMENTO) AS DATA,
                COUNT(*) AS QUANTIDADE_CONCLUIDA,
                COALESCE(SUM(S.VALOR_SERVICO), 0) AS FATURAMENTO
            FROM AGENDAMENTOS A
            INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
            WHERE S.USUARIO_FK = @UsuarioId
              AND A.DATA_AGENDAMENTO >= @Inicio
              AND A.DATA_AGENDAMENTO < @Fim
              AND UPPER(A.STATUS_AGENDAMENTO) = 'CONCLUIDO'
            GROUP BY DATE_TRUNC('day', A.DATA_AGENDAMENTO)
            ORDER BY DATA;

            SELECT
                A.DATA_AGENDAMENTO,
                COALESCE(CU.NOME_USUARIO, 'Cliente nao identificado') AS CLIENTE_NOME,
                COALESCE(PU.NOME_USUARIO, 'Prestador nao identificado') AS PRESTADOR_NOME,
                S.NOME_SERVICO,
                A.STATUS_AGENDAMENTO,
                S.VALOR_SERVICO
            FROM AGENDAMENTOS A
            INNER JOIN SERVICOS S ON S.ID_SERVICO = A.SERVICO_FK
            LEFT JOIN CLIENTES C ON C.ID_CLIENTE = A.CLIENTE_FK
            LEFT JOIN USUARIOS CU ON CU.ID_USUARIO = C.USUARIO_FK
            LEFT JOIN USUARIOS PU ON PU.ID_USUARIO = A.PRESTADOR_FK
            WHERE S.USUARIO_FK = @UsuarioId
              AND A.DATA_AGENDAMENTO >= @Inicio
              AND A.DATA_AGENDAMENTO < @Fim
            ORDER BY A.DATA_AGENDAMENTO;";

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("@UsuarioId", usuarioId);
        command.Parameters.Add("@Inicio", NpgsqlDbType.Timestamp).Value = inicio;
        command.Parameters.Add("@Fim", NpgsqlDbType.Timestamp).Value = fim;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new ArgumentException("Usuario nao encontrado.");

        var tipoUsuario = TipoUsuarioExtensions.FromDatabaseValue(
            reader.GetString(reader.GetOrdinal("TIPO_USUARIO")));

        if (tipoUsuario is not (TipoUsuario.EMPRESA or TipoUsuario.AUTONOMO))
            throw new UnauthorizedAccessException(
                "Apenas empresas e autonomos podem acessar relatorios.");

        var relatorio = new RelatorioMensalModel
        {
            ResponsavelNome = reader.GetString(reader.GetOrdinal("NOME_USUARIO")),
            ResponsavelTipo = tipoUsuario.ToDatabaseValue(),
            PeriodoInicio = inicio,
            PeriodoFim = fim.AddDays(-1)
        };

        await reader.NextResultAsync();
        if (await reader.ReadAsync())
        {
            relatorio.TotalAgendamentos = Convert.ToInt32(
                reader.GetInt64(reader.GetOrdinal("TOTAL_AGENDAMENTOS")));
            relatorio.TotalAgendados = Convert.ToInt32(
                reader.GetInt64(reader.GetOrdinal("TOTAL_AGENDADOS")));
            relatorio.TotalConcluidos = Convert.ToInt32(
                reader.GetInt64(reader.GetOrdinal("TOTAL_CONCLUIDOS")));
            relatorio.TotalCancelados = Convert.ToInt32(
                reader.GetInt64(reader.GetOrdinal("TOTAL_CANCELADOS")));
            relatorio.FaturamentoTotal = reader.GetDecimal(
                reader.GetOrdinal("FATURAMENTO_TOTAL"));
            relatorio.TicketMedio = relatorio.TotalConcluidos > 0
                ? relatorio.FaturamentoTotal / relatorio.TotalConcluidos
                : 0;
        }

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            relatorio.Servicos.Add(new RelatorioServicoModel
            {
                Nome = reader.GetString(reader.GetOrdinal("NOME_SERVICO")),
                QuantidadeConcluida = Convert.ToInt32(
                    reader.GetInt64(reader.GetOrdinal("QUANTIDADE_CONCLUIDA"))),
                Faturamento = reader.GetDecimal(reader.GetOrdinal("FATURAMENTO"))
            });
        }

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            relatorio.FaturamentoPorDia.Add(new RelatorioFaturamentoDiaModel
            {
                Data = reader.GetDateTime(reader.GetOrdinal("DATA")),
                QuantidadeConcluida = Convert.ToInt32(
                    reader.GetInt64(reader.GetOrdinal("QUANTIDADE_CONCLUIDA"))),
                Faturamento = reader.GetDecimal(reader.GetOrdinal("FATURAMENTO"))
            });
        }

        await reader.NextResultAsync();
        while (await reader.ReadAsync())
        {
            relatorio.Agendamentos.Add(new RelatorioAgendamentoItemModel
            {
                DataAgendamento = reader.GetDateTime(
                    reader.GetOrdinal("DATA_AGENDAMENTO")),
                ClienteNome = reader.GetString(reader.GetOrdinal("CLIENTE_NOME")),
                PrestadorNome = reader.GetString(reader.GetOrdinal("PRESTADOR_NOME")),
                ServicoNome = reader.GetString(reader.GetOrdinal("NOME_SERVICO")),
                Status = reader.GetString(reader.GetOrdinal("STATUS_AGENDAMENTO")),
                Valor = reader.GetDecimal(reader.GetOrdinal("VALOR_SERVICO"))
            });
        }

        return relatorio;
    }
}
