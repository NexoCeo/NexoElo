using Npgsql;
using NpgsqlTypes;

namespace SaaS.Api.Services;

/// <summary>Expira reservas nao finalizadas sem depender de acessos a tela ou novas reservas.</summary>
public sealed class ExpiracaoAgendamentosService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExpiracaoAgendamentosService> _logger;

    public ExpiracaoAgendamentosService(
        IConfiguration configuration,
        ILogger<ExpiracaoAgendamentosService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ProcessarAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await ProcessarAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ProcessarAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(@"
                UPDATE AGENDAMENTOS
                SET STATUS_AGENDAMENTO = 'CANCELADO',
                    MOTIVO_CANCELAMENTO = 'Cancelamento automático de agendamento!'
                WHERE UPPER(TRIM(STATUS_AGENDAMENTO)) = 'AGENDADO'
                  AND DATA_AGENDAMENTO <= @Limite", connection);
            var zona = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            var limite = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zona).AddHours(-24);
            command.Parameters.Add("@Limite", NpgsqlDbType.Timestamp).Value =
                DateTime.SpecifyKind(limite, DateTimeKind.Unspecified);
            var quantidade = await command.ExecuteNonQueryAsync(cancellationToken);
            if (quantidade > 0)
                _logger.LogInformation("{Quantidade} agendamentos vencidos foram cancelados automaticamente.", quantidade);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao executar cancelamento automatico de agendamentos.");
        }
    }
}
