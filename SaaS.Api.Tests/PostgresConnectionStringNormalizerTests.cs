using Npgsql;
using SaaS.Infrastructure.Configuration;
using Xunit;

namespace SaaS.Api.Tests;

public class PostgresConnectionStringNormalizerTests
{
    [Fact]
    public void MantemConnectionStringNpgsql()
    {
        const string original =
            "Host=db.example.test;Port=5432;Database=app;Username=user;Password=password";

        var result = PostgresConnectionStringNormalizer.Normalize(original);

        Assert.Equal(original, result);
    }

    [Fact]
    public void ConverteUrlFornecidaPeloRender()
    {
        const string url =
            "postgresql://app_user:senha%40segura@db.example.test:5433/app_db";

        var result = new NpgsqlConnectionStringBuilder(
            PostgresConnectionStringNormalizer.Normalize(url));

        Assert.Equal("db.example.test", result.Host);
        Assert.Equal(5433, result.Port);
        Assert.Equal("app_db", result.Database);
        Assert.Equal("app_user", result.Username);
        Assert.Equal("senha@segura", result.Password);
        Assert.Equal(SslMode.Require, result.SslMode);
    }
}
