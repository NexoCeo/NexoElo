using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;
using SaaS.Infrastructure.Services;
using Xunit;

namespace SaaS.Api.Tests;

public sealed class RelatoriosTests
{
    [Fact]
    public void ControllerPermiteSomenteEmpresaEAutonomo()
    {
        var authorize = typeof(RelatoriosController)
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal("EMPRESA,AUTONOMO", authorize!.Roles);
    }

    [Fact]
    public async Task ControllerUsaUsuarioDoTokenERetornaPdf()
    {
        var service = new Mock<IRelatorioService>();
        service
            .Setup(item => item.GerarRelatorioAsync(
                7,
                TipoRelatorio.RESUMO_FINANCEIRO,
                2026,
                8))
            .ReturnsAsync(new RelatorioArquivoModel
            {
                NomeArquivo = "resumo-financeiro-2026-08.pdf",
                Conteudo = Encoding.ASCII.GetBytes("%PDF-test")
            });

        var controller = new RelatoriosController(service.Object)
        {
            ControllerContext = CriarContexto(7)
        };

        var result = await controller.GerarRelatorio(
            "resumo-financeiro",
            2026,
            8);

        var arquivo = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", arquivo.ContentType);
        Assert.Equal("resumo-financeiro-2026-08.pdf", arquivo.FileDownloadName);
        service.VerifyAll();
    }

    [Fact]
    public async Task ControllerRejeitaTipoEPeriodoInvalidos()
    {
        var controller = new RelatoriosController(Mock.Of<IRelatorioService>())
        {
            ControllerContext = CriarContexto(7)
        };

        Assert.IsType<BadRequestObjectResult>(
            await controller.GerarRelatorio("inexistente", 2026, 8));
        Assert.IsType<BadRequestObjectResult>(
            await controller.GerarRelatorio("agenda-mensal", 2026, 13));
    }

    [Theory]
    [InlineData(TipoRelatorio.RESUMO_FINANCEIRO)]
    [InlineData(TipoRelatorio.SERVICOS_MAIS_REALIZADOS)]
    [InlineData(TipoRelatorio.AGENDA_MENSAL)]
    public async Task ServiceGeraPdfValidoParaCadaRelatorio(TipoRelatorio tipo)
    {
        var repository = new Mock<IRelatorioRepository>();
        repository
            .Setup(item => item.ObterRelatorioMensalAsync(7, 2026, 8))
            .ReturnsAsync(CriarDados());

        var service = new RelatorioService(repository.Object);

        var arquivo = await service.GerarRelatorioAsync(7, tipo, 2026, 8);

        Assert.EndsWith("-2026-08.pdf", arquivo.NomeArquivo);
        Assert.True(arquivo.Conteudo.Length > 500);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(arquivo.Conteudo, 0, 4));
    }

    private static ControllerContext CriarContexto(int usuarioId)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())],
            "Test");

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private static RelatorioMensalModel CriarDados()
    {
        return new RelatorioMensalModel
        {
            ResponsavelNome = "Empresa Teste",
            ResponsavelTipo = "EMPRESA",
            PeriodoInicio = new DateTime(2026, 8, 1),
            PeriodoFim = new DateTime(2026, 8, 31),
            TotalAgendamentos = 4,
            TotalAgendados = 1,
            TotalConcluidos = 2,
            TotalCancelados = 1,
            FaturamentoTotal = 250,
            TicketMedio = 125,
            Servicos =
            [
                new RelatorioServicoModel
                {
                    Nome = "Corte",
                    QuantidadeConcluida = 2,
                    Faturamento = 250
                }
            ],
            FaturamentoPorDia =
            [
                new RelatorioFaturamentoDiaModel
                {
                    Data = new DateTime(2026, 8, 10),
                    QuantidadeConcluida = 2,
                    Faturamento = 250
                }
            ],
            Agendamentos =
            [
                new RelatorioAgendamentoItemModel
                {
                    DataAgendamento = new DateTime(2026, 8, 10, 9, 0, 0),
                    ClienteNome = "Cliente Teste",
                    PrestadorNome = "Profissional Teste",
                    ServicoNome = "Corte",
                    Status = "CONCLUIDO",
                    Valor = 125
                }
            ]
        };
    }
}
