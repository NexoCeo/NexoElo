using Microsoft.Extensions.Configuration;
using Moq;
using SaaS.Api.Services;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public class PlanoAcessoServiceTests
{
    private static PlanoAcessoService Criar(UsuarioAssinaturaInfoModel assinatura)
    {
        var repo = new Mock<IAssinaturaRepository>();
        repo.Setup(x => x.ObterAssinaturaUsuario(assinatura.UsuarioId)).ReturnsAsync(assinatura);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:Prices:BASICO"] = "price_basic",
            ["Stripe:Prices:INTERMEDIARIO"] = "price_mid",
            ["Stripe:Prices:AVANCADO"] = "price_advanced"
        }).Build();
        return new PlanoAcessoService(repo.Object, config);
    }

    [Fact]
    public async Task UsuarioNovoRecebeGratisComAcessoIntegral()
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, DataCriacaoUsuario = DateTime.UtcNow.AddDays(-2),
            Status = StatusAssinatura.NAO_ATIVA
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.Equal("GRATIS", acesso.Plano);
        Assert.True(acesso.AcessoAtivo);
        Assert.True(acesso.RelatoriosLiberados);
        Assert.Equal(int.MaxValue, acesso.LimiteServicos);
    }

    [Fact]
    public async Task GratisExpiraAposTrintaDias()
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, DataCriacaoUsuario = DateTime.UtcNow.AddDays(-31),
            Status = StatusAssinatura.NAO_ATIVA
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.False(acesso.AcessoAtivo);
        Assert.Equal("SEM_PLANO", acesso.Plano);
    }

    [Theory]
    [InlineData("price_basic", "BASICO", 4, 2, false)]
    [InlineData("price_mid", "INTERMEDIARIO", 8, 4, true)]
    [InlineData("price_advanced", "AVANCADO", int.MaxValue, int.MaxValue, true)]
    public async Task AssinaturaPagaDefineLimites(string price, string plano, int servicos, int profissionais, bool relatorios)
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, Status = StatusAssinatura.ATIVA,
            StripePriceId = price, StripeSubscriptionId = "sub_123",
            DataFimAssinatura = DateTime.UtcNow.AddDays(12)
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.Equal(plano, acesso.Plano);
        Assert.Equal(servicos, acesso.LimiteServicos);
        Assert.Equal(profissionais, acesso.LimiteProfissionais);
        Assert.Equal(relatorios, acesso.RelatoriosLiberados);
    }

    [Fact]
    public async Task CancelamentoNaoReiniciaGratis()
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, Status = StatusAssinatura.CANCELADA,
            StripeSubscriptionId = "sub_cancelada",
            DataCriacaoUsuario = DateTime.UtcNow.AddDays(-1)
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.False(acesso.AcessoAtivo);
    }
    [Fact]
    public async Task GratisNaoDependeDeRegistroNaStripe()
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, DataCriacaoUsuario = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-5), DateTimeKind.Unspecified),
            Status = StatusAssinatura.NAO_ATIVA
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.Equal("GRATIS", acesso.Plano);
        Assert.Equal("TRIAL", acesso.Status);
        Assert.True(acesso.AcessoAtivo);
        Assert.InRange(acesso.DataFim!.Value, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
    }

    [Fact]
    public async Task GratisExpiradoTemStatusEspecifico()
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, DataCriacaoUsuario = DateTime.UtcNow.AddDays(-31),
            Status = StatusAssinatura.NAO_ATIVA
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.Equal("TRIAL_EXPIRADO", acesso.Status);
        Assert.False(acesso.AcessoAtivo);
    }

    [Fact]
    public async Task CheckoutPendenteNaoLiberaPlanoPagoNemReiniciaGratis()
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, DataCriacaoUsuario = DateTime.UtcNow.AddDays(-1),
            Status = StatusAssinatura.PENDENTE, StripePriceId = "price_basic"
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.Equal("SEM_PLANO", acesso.Plano);
        Assert.False(acesso.AcessoAtivo);
        Assert.Equal("PENDENTE", acesso.Status);
    }

    [Fact]
    public async Task AssinaturaExpiradaNaoVoltaAoGratis()
    {
        var service = Criar(new UsuarioAssinaturaInfoModel {
            UsuarioId = 1, DataCriacaoUsuario = DateTime.UtcNow.AddDays(-1),
            Status = StatusAssinatura.EXPIRADO, StripePriceId = "price_basic",
            StripeSubscriptionId = "sub_expirada", DataFimAssinatura = DateTime.UtcNow.AddHours(-1)
        });
        var acesso = await service.ConsultarAsync(1);
        Assert.Equal("SEM_PLANO", acesso.Plano);
        Assert.False(acesso.AcessoAtivo);
    }

}
