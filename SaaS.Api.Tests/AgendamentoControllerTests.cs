using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Api.Hubs;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Enums;
using SaaS.Domain.Exceptions;
using SaaS.Domain.Models;
using SaaS.Domain.Rules;
using Xunit;

namespace SaaS.Api.Tests;

public class AgendamentoControllerTests
{
    [Fact]
    public async Task CriarAgendamentoNotificaEmpresaEProfissional()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.ObterTipoUsuarioAsync(7))
            .ReturnsAsync(TipoUsuario.EMPRESA);
        repository
            .Setup(item => item.CriarAgendamentoAsync(It.IsAny<AgendamentoModel>()))
            .ReturnsAsync((AgendamentoModel item) =>
            {
                item.Id = 42;
                return item;
            });

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(item => item.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hubClients = new Mock<IHubClients>();
        hubClients
            .Setup(item => item.Group(It.IsAny<string>()))
            .Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<AgendaHub>>();
        hubContext.SetupGet(item => item.Clients).Returns(hubClients.Object);

        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            hubContext.Object,
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(4)
        };
        var requestedDate = new DateTime(2026, 8, 16, 8, 0, 0);

        var result = await controller.CriarAgendamento(new CreateAgendamentoModel
        {
            ClienteId = 4,
            ResponsavelId = 7,
            ProfissionalId = 9,
            ServicoId = 2,
            DataAgendamento = requestedDate
        });

        Assert.IsType<OkObjectResult>(result);
        hubClients.Verify(item => item.Group(AgendaHub.GrupoUsuario(4)), Times.Once);
        hubClients.Verify(item => item.Group(AgendaHub.GrupoUsuario(7)), Times.Once);
        hubClients.Verify(item => item.Group(AgendaHub.GrupoUsuario(9)), Times.Once);
        clientProxy.Verify(item => item.SendCoreAsync(
            AgendaHub.EventoAgendaAtualizada,
            It.Is<object?[]>(arguments =>
                arguments.Length == 1 &&
                arguments[0] != null &&
                arguments[0]!.GetType().GetProperty("Data")!.GetValue(arguments[0])!.ToString() == "2026-08-16"),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task CriarAgendamentoMantemSucessoQuandoONotificadorFalha()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.ObterTipoUsuarioAsync(7))
            .ReturnsAsync(TipoUsuario.EMPRESA);
        repository
            .Setup(item => item.CriarAgendamentoAsync(It.IsAny<AgendamentoModel>()))
            .ReturnsAsync((AgendamentoModel item) =>
            {
                item.Id = 43;
                return item;
            });

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(item => item.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Hub indisponivel"));

        var hubClients = new Mock<IHubClients>();
        hubClients
            .Setup(item => item.Group(It.IsAny<string>()))
            .Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<AgendaHub>>();
        hubContext.SetupGet(item => item.Clients).Returns(hubClients.Object);

        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            hubContext.Object,
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(4)
        };

        var result = await controller.CriarAgendamento(new CreateAgendamentoModel
        {
            ClienteId = 4,
            ResponsavelId = 7,
            ProfissionalId = 9,
            ServicoId = 2,
            DataAgendamento = new DateTime(2026, 8, 16, 9, 0, 0)
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var createdAppointment = Assert.IsType<AgendamentoModel>(okResult.Value);
        Assert.Equal(43, createdAppointment.Id);
    }

    [Fact]
    public async Task ListarHorariosDisponiveisRetornaSomenteAsOpcoesValidadasPeloRepositorio()
    {
        var data = new DateTime(2026, 8, 17);
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.ListarHorariosDisponiveis(7, 9, 2, data))
            .ReturnsAsync(new List<HorarioDisponivelModel>
            {
                new() { DataAgendamento = data.AddHours(8), Horario = "08:00" },
                new() { DataAgendamento = data.AddHours(8).AddMinutes(30), Horario = "08:30" }
            });

        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            Mock.Of<IHubContext<AgendaHub>>(),
            Mock.Of<ILogger<AgendamentoController>>());

        var result = await controller.ListarHorariosDisponiveis(7, 9, 2, data);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var horarios = Assert.IsType<List<HorarioDisponivelModel>>(okResult.Value);
        Assert.Collection(
            horarios,
            horario => Assert.Equal("08:00", horario.Horario),
            horario => Assert.Equal("08:30", horario.Horario));
    }

    [Fact]
    public async Task ListarAgendaPorDataPermiteSomenteOProprioUsuario()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.ListarAgendamentosPorData(7, new DateTime(2026, 8, 16), null))
            .ReturnsAsync(new List<AgendamentoModel>
            {
                new() { Id = 6, ClienteNome = "Cliente", ServicoNome = "Servico" }
            });

        var hubContext = new Mock<IHubContext<AgendaHub>>();
        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            hubContext.Object,
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(7)
        };

        var allowed = await controller.ListarAgendaPorData(7, new DateTime(2026, 8, 16));
        Assert.IsType<OkObjectResult>(allowed);

        controller.ControllerContext = CreateControllerContext(8);
        var forbidden = await controller.ListarAgendaPorData(7, new DateTime(2026, 8, 16));
        Assert.IsType<ForbidResult>(forbidden);

        repository.Verify(
            item => item.ListarAgendamentosPorData(7, new DateTime(2026, 8, 16), null),
            Times.Once);
    }

    [Fact]
    public async Task ListarHistoricoPermiteSomenteOProprioUsuario()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.ListarHistoricoAgendamentos(4))
            .ReturnsAsync(new List<AgendamentoModel>
            {
                new() { Id = 12, ClienteId = 4, ServicoNome = "Corte" }
            });

        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            Mock.Of<IHubContext<AgendaHub>>(),
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(4)
        };

        var allowed = await controller.GetHistoricoAgendamentos(4);
        Assert.IsType<OkObjectResult>(allowed);

        controller.ControllerContext = CreateControllerContext(5);
        var forbidden = await controller.GetHistoricoAgendamentos(4);
        Assert.IsType<ForbidResult>(forbidden);

        repository.Verify(item => item.ListarHistoricoAgendamentos(4), Times.Once);
    }

    [Fact]
    public async Task CriarAgendamentoUsaClienteDoTokenEIgnoraIdDoPayload()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.ObterTipoUsuarioAsync(7))
            .ReturnsAsync(TipoUsuario.EMPRESA);
        repository
            .Setup(item => item.CriarAgendamentoAsync(It.IsAny<AgendamentoModel>()))
            .ReturnsAsync((AgendamentoModel item) => item);
        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            Mock.Of<IHubContext<AgendaHub>>(),
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(5)
        };

        var result = await controller.CriarAgendamento(new CreateAgendamentoModel
        {
            ClienteId = 4,
            ResponsavelId = 7,
            ProfissionalId = 9,
            ServicoId = 2,
            DataAgendamento = DateTime.UtcNow.AddDays(1)
        });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(
            item => item.CriarAgendamentoAsync(
                It.Is<AgendamentoModel>(agendamento => agendamento.ClienteId == 5)),
            Times.Once);
    }

    [Fact]
    public async Task AtualizarStatusUsaUsuarioDoTokenENotificaOsEnvolvidos()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.AtualizarStatusAsync(12, 4, "CANCELADO"))
            .ReturnsAsync(new AgendamentoModel
            {
                Id = 12,
                ClienteId = 4,
                EmpresaId = 7,
                ProfissionalId = 9,
                DataAgendamento = new DateTime(2026, 8, 20, 8, 30, 0),
                Status = "CANCELADO"
            });

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(item => item.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hubClients = new Mock<IHubClients>();
        hubClients
            .Setup(item => item.Group(It.IsAny<string>()))
            .Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<AgendaHub>>();
        hubContext.SetupGet(item => item.Clients).Returns(hubClients.Object);

        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            hubContext.Object,
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(4)
        };

        var result = await controller.AtualizarStatus(
            12,
            new AtualizarStatusAgendamentoModel { Status = "CANCELADO" });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var agendamento = Assert.IsType<AgendamentoModel>(okResult.Value);
        Assert.Equal("CANCELADO", agendamento.Status);
        repository.Verify(item => item.AtualizarStatusAsync(12, 4, "CANCELADO"), Times.Once);
        hubClients.Verify(item => item.Group(AgendaHub.GrupoUsuario(4)), Times.Once);
        hubClients.Verify(item => item.Group(AgendaHub.GrupoUsuario(7)), Times.Once);
        hubClients.Verify(item => item.Group(AgendaHub.GrupoUsuario(9)), Times.Once);
    }

    [Fact]
    public async Task AtualizarStatusRetornaConflitoQuandoRegraImpedeCancelamento()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.AtualizarStatusAsync(12, 4, "CANCELADO"))
            .ThrowsAsync(new RegraAgendamentoException(
                "Limite de prazo atingido, o agendamento não pode ser cancelado."));

        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            Mock.Of<IHubContext<AgendaHub>>(),
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(4)
        };

        var result = await controller.AtualizarStatus(
            12,
            new AtualizarStatusAgendamentoModel { Status = "CANCELADO" });

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task ConcluirAgendamentosDoDiaUsaUsuarioAutenticado()
    {
        var repository = new Mock<IAgendamentoRepository>();
        repository
            .Setup(item => item.ConcluirAgendamentosDoDiaAsync(7))
            .ReturnsAsync(3);

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(item => item.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var hubClients = new Mock<IHubClients>();
        hubClients
            .Setup(item => item.Group(AgendaHub.GrupoUsuario(7)))
            .Returns(clientProxy.Object);

        var hubContext = new Mock<IHubContext<AgendaHub>>();
        hubContext.SetupGet(item => item.Clients).Returns(hubClients.Object);

        var controller = new AgendamentoController(
            repository.Object,
            Mock.Of<IVinculoRepository>(),
            hubContext.Object,
            Mock.Of<ILogger<AgendamentoController>>())
        {
            ControllerContext = CreateControllerContext(7)
        };

        var result = await controller.ConcluirAgendamentosDoDiaAtual();

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3, okResult.Value!.GetType().GetProperty("quantidade")!.GetValue(okResult.Value));
        repository.Verify(item => item.ConcluirAgendamentosDoDiaAsync(7), Times.Once);
    }

    [Fact]
    public void ClientePodeCancelarExatamenteDuasHorasAntes()
    {
        var dataAgendamento = new DateTime(2026, 8, 20, 10, 0, 0);

        var status = AgendamentoStatusPolicy.ValidarTransicao(
            "AGENDADO",
            "CANCELADO",
            TipoUsuario.CLIENTE,
            dataAgendamento,
            dataAgendamento.AddHours(-2));

        Assert.Equal("CANCELADO", status);
    }

    [Fact]
    public void ClienteNaoPodeCancelarDepoisDoLimiteDeDuasHoras()
    {
        var dataAgendamento = new DateTime(2026, 8, 20, 10, 0, 0);

        var exception = Assert.Throws<RegraAgendamentoException>(() =>
            AgendamentoStatusPolicy.ValidarTransicao(
                "AGENDADO",
                "CANCELADO",
                TipoUsuario.CLIENTE,
                dataAgendamento,
                dataAgendamento.AddHours(-2).AddSeconds(1)));

        Assert.Equal(
            "Limite de prazo atingido, o agendamento não pode ser cancelado.",
            exception.Message);
    }

    [Fact]
    public void StatusFinalizadoNaoPodeSerAlteradoParaOutroStatus()
    {
        Assert.Throws<RegraAgendamentoException>(() =>
            AgendamentoStatusPolicy.ValidarTransicao(
                "CONCLUIDO",
                "CANCELADO",
                TipoUsuario.EMPRESA,
                new DateTime(2026, 8, 20, 10, 0, 0),
                new DateTime(2026, 8, 20, 8, 0, 0)));
    }

    private static ControllerContext CreateControllerContext(int usuarioId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()) },
            "Test");

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }
}
