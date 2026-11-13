using Microsoft.AspNetCore.Mvc;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public class ProfissionalEmpresasCadastroTests
{
    [Fact]
    public async Task ListaEmpresasPublicasSemAguardarCidade()
    {
        var repo = new Mock<IProfissionalRepository>();
        var empresas = new List<EmpresaCidadeModel>
        {
            new() { IdUsuario = 2, Nome = "Barbearia Shop" },
            new() { IdUsuario = 4, Nome = "Estudio Teste" }
        };
        repo.Setup(item => item.ListarEmpresasDisponiveisParaCadastro())
            .ReturnsAsync(empresas);

        var controller = new ProfissionalController(repo.Object, Mock.Of<IVinculoRepository>());
        var result = await controller.ListarEmpresasPorCidade(null);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(empresas, ok.Value);
        repo.Verify(item => item.ListarEmpresasDisponiveisParaCadastro(), Times.Once);
        repo.Verify(item => item.ListarEmpresasPorCidade(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ConsultaComCidadeContinuaFiltrada()
    {
        var repo = new Mock<IProfissionalRepository>();
        var empresas = new List<EmpresaCidadeModel>
        {
            new() { IdUsuario = 3, Nome = "Empresa Local" }
        };
        repo.Setup(item => item.ListarEmpresasPorCidade(77)).ReturnsAsync(empresas);

        var controller = new ProfissionalController(repo.Object, Mock.Of<IVinculoRepository>());
        var result = await controller.ListarEmpresasPorCidade(77);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(empresas, ok.Value);
        repo.Verify(item => item.ListarEmpresasPorCidade(77), Times.Once);
        repo.Verify(item => item.ListarEmpresasDisponiveisParaCadastro(), Times.Never);
    }

    [Fact]
    public async Task ConsultaComCidadeInvalidaRetornaBadRequest()
    {
        var repo = new Mock<IProfissionalRepository>();
        var controller = new ProfissionalController(repo.Object, Mock.Of<IVinculoRepository>());

        var result = await controller.ListarEmpresasPorCidade(0);

        Assert.IsType<BadRequestObjectResult>(result);
        repo.VerifyNoOtherCalls();
    }
}
