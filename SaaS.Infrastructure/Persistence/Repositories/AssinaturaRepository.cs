using Microsoft.Extensions.Configuration;
using SaaS.Domain.Models;
using SaaS.Domain.Enums;
using SaaS.Application.Interfaces.Repositories;
using Npgsql;

namespace SaaS.Infrastructure.Persistence.Repositories
{
    public class AssinaturaRepository : IAssinaturaRepository
    {
        private readonly string _connectionString;

        public AssinaturaRepository(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new ArgumentNullException(nameof(configuration), "Connection string nao pode ser nula.");
        }

        public async Task<StatusAssinatura> ObterStatusAssinatura(int usuarioId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT A.STATUS_ASSINATURA
                FROM ASSINATURAS A
                WHERE A.USUARIO_FK = @Id";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", usuarioId);

            var result = await command.ExecuteScalarAsync();
            return StatusAssinaturaExtensions.FromDatabaseValue(result?.ToString());
        }

        public async Task<UsuarioAssinaturaInfoModel?> ObterAssinaturaUsuario(int usuarioId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                SELECT
                    U.ID_USUARIO,
                    U.NOME_USUARIO,
                    U.EMAIL_USUARIO,
                    COALESCE(A.STATUS_ASSINATURA, 'NAO_ATIVA') AS STATUS_ASSINATURA,
                    A.DATA_FIM_ASSINATURA,
                    A.STRIPE_CUSTOMER_ID,
                    A.STRIPE_SUBSCRIPTION_ID,
                    A.STRIPE_PRICE_ID
                FROM USUARIOS U
                LEFT JOIN ASSINATURAS A ON A.USUARIO_FK = U.ID_USUARIO
                WHERE U.ID_USUARIO = @Id";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Id", usuarioId);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;

            return new UsuarioAssinaturaInfoModel
            {
                UsuarioId = reader.GetInt32(reader.GetOrdinal("ID_USUARIO")),
                Nome = reader.GetString(reader.GetOrdinal("NOME_USUARIO")),
                Email = GetNullableString(reader, "EMAIL_USUARIO"),
                Status = StatusAssinaturaExtensions.FromDatabaseValue(GetNullableString(reader, "STATUS_ASSINATURA")),
                DataFimAssinatura = GetNullableDateTime(reader, "DATA_FIM_ASSINATURA"),
                StripeCustomerId = GetNullableString(reader, "STRIPE_CUSTOMER_ID"),
                StripeSubscriptionId = GetNullableString(reader, "STRIPE_SUBSCRIPTION_ID"),
                StripePriceId = GetNullableString(reader, "STRIPE_PRICE_ID")
            };
        }

        public async Task<bool> AtualizarStatusAssinatura(int usuarioId, StatusAssinatura novoStatus)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                INSERT INTO ASSINATURAS (USUARIO_FK, STATUS_ASSINATURA, DATA_ALTERACAO_ASSINATURA)
                SELECT ID_USUARIO, @Status, CURRENT_TIMESTAMP
                FROM USUARIOS
                WHERE ID_USUARIO = @Id
                ON CONFLICT (USUARIO_FK)
                DO UPDATE SET
                    STATUS_ASSINATURA = EXCLUDED.STATUS_ASSINATURA,
                    DATA_ALTERACAO_ASSINATURA = CURRENT_TIMESTAMP";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Status", novoStatus.ToDatabaseValue());
            command.Parameters.AddWithValue("@Id", usuarioId);

            var affected = await command.ExecuteNonQueryAsync();
            return affected > 0;
        }

        public async Task<bool> AtualizarClienteStripe(int usuarioId, string stripeCustomerId)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                INSERT INTO ASSINATURAS (USUARIO_FK, STATUS_ASSINATURA, STRIPE_CUSTOMER_ID, DATA_ALTERACAO_ASSINATURA)
                SELECT ID_USUARIO, @Status, @StripeCustomerId, CURRENT_TIMESTAMP
                FROM USUARIOS
                WHERE ID_USUARIO = @Id
                ON CONFLICT (USUARIO_FK)
                DO UPDATE SET
                    STRIPE_CUSTOMER_ID = EXCLUDED.STRIPE_CUSTOMER_ID,
                    DATA_ALTERACAO_ASSINATURA = CURRENT_TIMESTAMP";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Status", StatusAssinatura.NAO_ATIVA.ToDatabaseValue());
            command.Parameters.AddWithValue("@StripeCustomerId", stripeCustomerId);
            command.Parameters.AddWithValue("@Id", usuarioId);

            var affected = await command.ExecuteNonQueryAsync();
            return affected > 0;
        }

        public async Task<bool> AtualizarAssinaturaStripe(
            int usuarioId,
            StatusAssinatura status,
            string? stripeCustomerId,
            string? stripeSubscriptionId,
            string? stripePriceId,
            DateTime? dataFimAssinatura)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                INSERT INTO ASSINATURAS (
                    USUARIO_FK,
                    STATUS_ASSINATURA,
                    DATA_FIM_ASSINATURA,
                    STRIPE_CUSTOMER_ID,
                    STRIPE_SUBSCRIPTION_ID,
                    STRIPE_PRICE_ID,
                    DATA_ALTERACAO_ASSINATURA
                )
                SELECT
                    ID_USUARIO,
                    @Status,
                    @DataFimAssinatura,
                    @StripeCustomerId,
                    @StripeSubscriptionId,
                    @StripePriceId,
                    CURRENT_TIMESTAMP
                FROM USUARIOS
                WHERE ID_USUARIO = @Id
                ON CONFLICT (USUARIO_FK)
                DO UPDATE SET
                    STATUS_ASSINATURA = EXCLUDED.STATUS_ASSINATURA,
                    DATA_FIM_ASSINATURA = EXCLUDED.DATA_FIM_ASSINATURA,
                    STRIPE_CUSTOMER_ID = COALESCE(EXCLUDED.STRIPE_CUSTOMER_ID, ASSINATURAS.STRIPE_CUSTOMER_ID),
                    STRIPE_SUBSCRIPTION_ID = COALESCE(EXCLUDED.STRIPE_SUBSCRIPTION_ID, ASSINATURAS.STRIPE_SUBSCRIPTION_ID),
                    STRIPE_PRICE_ID = COALESCE(EXCLUDED.STRIPE_PRICE_ID, ASSINATURAS.STRIPE_PRICE_ID),
                    DATA_ALTERACAO_ASSINATURA = CURRENT_TIMESTAMP";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Status", status.ToDatabaseValue());
            command.Parameters.AddWithValue("@DataFimAssinatura", (object?)dataFimAssinatura ?? DBNull.Value);
            command.Parameters.AddWithValue("@StripeCustomerId", (object?)stripeCustomerId ?? DBNull.Value);
            command.Parameters.AddWithValue("@StripeSubscriptionId", (object?)stripeSubscriptionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@StripePriceId", (object?)stripePriceId ?? DBNull.Value);
            command.Parameters.AddWithValue("@Id", usuarioId);

            var affected = await command.ExecuteNonQueryAsync();
            return affected > 0;
        }

        public async Task<bool> AtualizarAssinaturaPorStripeSubscriptionId(
            string stripeSubscriptionId,
            StatusAssinatura status,
            string? stripeCustomerId,
            string? stripePriceId,
            DateTime? dataFimAssinatura)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            const string sql = @"
                UPDATE ASSINATURAS
                SET STATUS_ASSINATURA = @Status,
                    DATA_FIM_ASSINATURA = @DataFimAssinatura,
                    STRIPE_CUSTOMER_ID = COALESCE(@StripeCustomerId, STRIPE_CUSTOMER_ID),
                    STRIPE_PRICE_ID = COALESCE(@StripePriceId, STRIPE_PRICE_ID),
                    DATA_ALTERACAO_ASSINATURA = CURRENT_TIMESTAMP
                WHERE STRIPE_SUBSCRIPTION_ID = @StripeSubscriptionId";

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Status", status.ToDatabaseValue());
            command.Parameters.AddWithValue("@DataFimAssinatura", (object?)dataFimAssinatura ?? DBNull.Value);
            command.Parameters.AddWithValue("@StripeCustomerId", (object?)stripeCustomerId ?? DBNull.Value);
            command.Parameters.AddWithValue("@StripePriceId", (object?)stripePriceId ?? DBNull.Value);
            command.Parameters.AddWithValue("@StripeSubscriptionId", stripeSubscriptionId);

            var affected = await command.ExecuteNonQueryAsync();
            return affected > 0;
        }

        private static string? GetNullableString(NpgsqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static DateTime? GetNullableDateTime(NpgsqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
        }
    }
}
