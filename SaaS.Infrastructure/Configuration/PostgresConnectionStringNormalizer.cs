using Npgsql;

namespace SaaS.Infrastructure.Configuration;

public static class PostgresConnectionStringNormalizer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "Connection string DefaultConnection nao encontrada.");

        var connectionString = value.Trim();
        if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
            return connectionString;
        }

        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("A URL do PostgreSQL e invalida.");

        var userInfoSeparator = uri.UserInfo.IndexOf(':');
        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (userInfoSeparator <= 0 ||
            userInfoSeparator == uri.UserInfo.Length - 1 ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException(
                "A URL do PostgreSQL precisa conter usuario, senha, host e banco.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = database,
            Username = Uri.UnescapeDataString(uri.UserInfo[..userInfoSeparator]),
            Password = Uri.UnescapeDataString(uri.UserInfo[(userInfoSeparator + 1)..]),
            SslMode = SslMode.Require
        }.ConnectionString;
    }
}
