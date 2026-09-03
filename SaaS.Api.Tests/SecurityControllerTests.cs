using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Application.Exceptions;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;
using Xunit;

namespace SaaS.Api.Tests;

public class SecurityControllerTests
{
    [Fact]
    public async Task VincularServicosRejeitaOutraEmpresa()
    {
        var repository = new Mock<IVinculoRepository>();
        var controller = new VinculosController(repository.Object)
        {
            ControllerContext = CriarContexto(8, "EMPRESA")
        };

        var result = await controller.VincularServicosProfissional(
            9,
            7,
            new VincularServicosProfissionalModel { ServicoIds = new List<int> { 2 } });

        Assert.IsType<ForbidResult>(result);
        repository.Verify(
            item => item.VincularServicosProfissional(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IEnumerable<int>>()),
            Times.Never);
    }

    [Fact]
    public async Task InserirServicoIgnoraIdsDoPayloadEUsaUsuarioAutenticado()
    {
        var repository = new Mock<IServicoRepository>();
        repository
            .Setup(item => item.InserirServico(It.IsAny<ServicoModel>()))
            .ReturnsAsync((ServicoModel servico) => servico);
        var controller = new ServicoController(repository.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.InserirServico(new ServicoModel
        {
            UsuarioFk = 99,
            EmpresaId = 99,
            NomeServico = "Servico",
            Valor = 50,
            TempoEstimadoMinutos = 30
        });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(item => item.InserirServico(It.Is<ServicoModel>(servico =>
            servico.UsuarioFk == 7 &&
            servico.EmpresaId == 7 &&
            servico.ProfissionalId == null)), Times.Once);
    }

    [Fact]
    public async Task InserirServicoMultipartUsaUsuarioAutenticado()
    {
        var repository = new Mock<IServicoRepository>();
        repository
            .Setup(item => item.InserirServico(It.IsAny<ServicoModel>()))
            .ReturnsAsync((ServicoModel servico) => servico);
        var controller = new ServicoController(repository.Object)
        {
            ControllerContext = CriarContexto(7, "AUTONOMO")
        };

        var result = await controller.InserirServicoComImagem(new CreateServicoModel
        {
            NomeServico = "Corte",
            Valor = 50,
            TempoEstimadoMinutos = 30
        });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(item => item.InserirServico(It.Is<ServicoModel>(servico =>
            servico.UsuarioFk == 7 &&
            servico.ProfissionalId == 7 &&
            servico.EmpresaId == null)), Times.Once);
    }

    [Fact]
    public async Task InserirServicoMultipartPersisteImagemSelecionada()
    {
        var repository = new Mock<IServicoRepository>();
        var uploadService = new Mock<IArquivoUploadService>();
        repository
            .Setup(item => item.InserirServico(It.IsAny<ServicoModel>()))
            .ReturnsAsync((ServicoModel servico) => servico);
        uploadService
            .Setup(item => item.SalvarAsync(
                It.IsAny<byte[]>(),
                ".png",
                "image/png",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("uploads/servico-real.png");
        var controller = new ServicoController(repository.Object, uploadService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };
        var bytesImagem = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01
        };
        var conteudo = new MemoryStream(bytesImagem);
        var arquivo = new FormFile(conteudo, 0, conteudo.Length, "ImagemServico", "servico.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.InserirServicoComImagem(new CreateServicoModel
        {
            NomeServico = "Corte",
            Valor = 50,
            TempoEstimadoMinutos = 30,
            ImagemServico = arquivo
        });

        Assert.IsType<OkObjectResult>(result);
        uploadService.Verify(item => item.SalvarAsync(
            It.Is<byte[]>(bytes => bytes.SequenceEqual(bytesImagem)),
            ".png",
            "image/png",
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.InserirServico(
            It.Is<ServicoModel>(servico =>
                servico.ImagemServico == "uploads/servico-real.png")), Times.Once);
    }

    [Fact]
    public async Task InserirServicoMultipartRejeitaArquivoQueNaoEhImagem()
    {
        var repository = new Mock<IServicoRepository>();
        var controller = new ServicoController(repository.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };
        var conteudo = new MemoryStream("arquivo-invalido"u8.ToArray());
        var arquivo = new FormFile(conteudo, 0, conteudo.Length, "ImagemServico", "servico.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.InserirServicoComImagem(new CreateServicoModel
        {
            NomeServico = "Corte",
            Valor = 50,
            ImagemServico = arquivo
        });

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(item => item.InserirServico(It.IsAny<ServicoModel>()), Times.Never);
    }

    [Fact]
    public async Task AtualizarServicoUsaProprietarioAutenticado()
    {
        var repository = new Mock<IServicoRepository>();
        repository
            .Setup(item => item.ListarServicos(7))
            .ReturnsAsync(new List<ServicoModel>
            {
                new() { Id = 2, UsuarioFk = 7, NomeServico = "Corte", Valor = 50 }
            });
        repository
            .Setup(item => item.AtualizarServico(2, 7, It.IsAny<ServicoModel>(), false))
            .ReturnsAsync((int id, int usuarioId, ServicoModel servico, bool _) =>
            {
                servico.Id = id;
                servico.UsuarioFk = usuarioId;
                return servico;
            });
        var controller = new ServicoController(repository.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.AtualizarServico(2, new UpdateServicoModel
        {
            NomeServico = "Corte premium",
            Valor = 80,
            TempoEstimadoMinutos = 45
        });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(item => item.AtualizarServico(
            2,
            7,
            It.Is<ServicoModel>(servico =>
                servico.UsuarioFk == 7 &&
                servico.EmpresaId == 7 &&
                servico.ProfissionalId == null &&
                servico.NomeServico == "Corte premium"),
            false), Times.Once);
    }

    [Fact]
    public async Task AtualizarServicoSubstituiImagemPersistida()
    {
        var repository = new Mock<IServicoRepository>();
        var uploadService = new Mock<IArquivoUploadService>();
        repository
            .Setup(item => item.ListarServicos(7))
            .ReturnsAsync(new List<ServicoModel>
            {
                new()
                {
                    Id = 2,
                    UsuarioFk = 7,
                    NomeServico = "Corte",
                    Valor = 50,
                    ImagemServico = "uploads/servico-antigo.png"
                }
            });
        uploadService
            .Setup(item => item.SalvarAsync(
                It.IsAny<byte[]>(),
                ".png",
                "image/png",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("uploads/servico-novo.png");
        repository
            .Setup(item => item.AtualizarServico(2, 7, It.IsAny<ServicoModel>(), true))
            .ReturnsAsync((int id, int usuarioId, ServicoModel servico, bool _) =>
            {
                servico.Id = id;
                servico.UsuarioFk = usuarioId;
                return servico;
            });
        var controller = new ServicoController(repository.Object, uploadService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };
        var bytesImagem = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01
        };
        var conteudo = new MemoryStream(bytesImagem);
        var arquivo = new FormFile(conteudo, 0, conteudo.Length, "ImagemServico", "novo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.AtualizarServico(2, new UpdateServicoModel
        {
            NomeServico = "Corte premium",
            Valor = 80,
            TempoEstimadoMinutos = 45,
            ImagemServico = arquivo
        });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(item => item.AtualizarServico(
            2,
            7,
            It.Is<ServicoModel>(servico =>
                servico.ImagemServico == "uploads/servico-novo.png"),
            true), Times.Once);
        uploadService.Verify(item => item.RemoverAsync(
            "uploads/servico-antigo.png",
            It.IsAny<CancellationToken>()), Times.Once);
        uploadService.Verify(item => item.RemoverAsync(
            "uploads/servico-novo.png",
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AtualizarServicoNaoPermiteServicoDeOutroUsuario()
    {
        var repository = new Mock<IServicoRepository>();
        repository.Setup(item => item.ListarServicos(7)).ReturnsAsync(new List<ServicoModel>());
        var controller = new ServicoController(repository.Object)
        {
            ControllerContext = CriarContexto(7, "AUTONOMO")
        };

        var result = await controller.AtualizarServico(2, new UpdateServicoModel
        {
            NomeServico = "Servico",
            Valor = 50,
            TempoEstimadoMinutos = 30
        });

        Assert.IsType<NotFoundObjectResult>(result);
        repository.Verify(item => item.AtualizarServico(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<ServicoModel>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task FuncionamentoRejeitaUsuarioDiferenteDoToken()
    {
        var repository = new Mock<IFuncionamentoRepository>();
        var controller = new FuncionamentoController(repository.Object)
        {
            ControllerContext = CriarContexto(8, "EMPRESA")
        };

        var result = await controller.ObterFuncionamento(7);

        Assert.IsType<ForbidResult>(result);
        repository.Verify(
            item => item.ObterFuncionamento(It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task UsuarioPorIdNaoExpoeHashDeSenha()
    {
        var repository = new Mock<IUsuarioRepository>();
        repository.Setup(item => item.ListarUsuarioPorId(7)).ReturnsAsync(new UsuarioModel
        {
            Id = 7,
            Nome = "Empresa",
            Senha = "hash-que-nao-pode-sair",
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.EMPRESA
        });
        var controller = new UsuarioController(
            repository.Object,
            Mock.Of<ILocalizacaoCadastroService>(),
            Mock.Of<IEmailService>())
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.ListarUsuarioPorId(7);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Null(ok.Value!.GetType().GetProperty("Senha"));
    }

    [Fact]
    public async Task AtualizarPerfilRetornaUsuarioAtualizado()
    {
        var repository = new Mock<IUsuarioRepository>();
        repository.Setup(item => item.ListarUsuarioPorId(7)).ReturnsAsync(new UsuarioModel
        {
            Id = 7,
            Nome = "Empresa",
            Email = "empresa@mail.com",
            FotoPerfil = "uploads/antiga.png",
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.EMPRESA
        });
        repository
            .Setup(item => item.AtualizarPerfilUsuario(
                7,
                "Empresa Renovada",
                "nova@mail.com",
                "27999999999",
                null,
                false))
            .ReturnsAsync(new UsuarioModel
            {
                Id = 7,
                Nome = "EMPRESA RENOVADA",
                Email = "NOVA@MAIL.COM",
                Telefone = "27999999999",
                FotoPerfil = "uploads/antiga.png",
                TipoUsuario = SaaS.Domain.Enums.TipoUsuario.EMPRESA
            });
        var controller = new UsuarioController(
            repository.Object,
            Mock.Of<ILocalizacaoCadastroService>(),
            Mock.Of<IEmailService>())
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.AtualizarPerfil(7, new UpdatePerfilModel
        {
            Nome = "Empresa Renovada",
            Email = "nova@mail.com",
            Telefone = "27999999999"
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(
            "EMPRESA RENOVADA",
            ok.Value!.GetType().GetProperty("Nome")!.GetValue(ok.Value));
    }

    [Fact]
    public async Task AtualizarPerfilSubstituiFotoPersistida()
    {
        var repository = new Mock<IUsuarioRepository>();
        var uploadService = new Mock<IArquivoUploadService>();
        repository.Setup(item => item.ListarUsuarioPorId(7)).ReturnsAsync(new UsuarioModel
        {
            Id = 7,
            Nome = "Empresa",
            Email = "empresa@mail.com",
            FotoPerfil = "uploads/perfil-antigo.png",
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.EMPRESA
        });
        uploadService
            .Setup(item => item.SalvarAsync(
                It.IsAny<byte[]>(),
                ".png",
                "image/png",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("uploads/perfil-novo.png");
        repository
            .Setup(item => item.AtualizarPerfilUsuario(
                7,
                "Empresa Renovada",
                "nova@mail.com",
                "27999999999",
                "uploads/perfil-novo.png",
                true))
            .ReturnsAsync(new UsuarioModel
            {
                Id = 7,
                Nome = "EMPRESA RENOVADA",
                Email = "NOVA@MAIL.COM",
                Telefone = "27999999999",
                FotoPerfil = "uploads/perfil-novo.png",
                TipoUsuario = SaaS.Domain.Enums.TipoUsuario.EMPRESA
            });
        var controller = new UsuarioController(
            repository.Object,
            Mock.Of<ILocalizacaoCadastroService>(),
            Mock.Of<IEmailService>(),
            arquivoUploadService: uploadService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };
        var bytesImagem = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01
        };
        var conteudo = new MemoryStream(bytesImagem);
        var arquivo = new FormFile(conteudo, 0, conteudo.Length, "FotoPerfil", "novo.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.AtualizarPerfil(7, new UpdatePerfilModel
        {
            Nome = "Empresa Renovada",
            Email = "nova@mail.com",
            Telefone = "27999999999",
            FotoPerfil = arquivo
        });

        Assert.IsType<OkObjectResult>(result);
        uploadService.Verify(item => item.RemoverAsync(
            "uploads/perfil-antigo.png",
            It.IsAny<CancellationToken>()), Times.Once);
        uploadService.Verify(item => item.RemoverAsync(
            "uploads/perfil-novo.png",
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AtualizarPerfilRejeitaOutroUsuario()
    {
        var repository = new Mock<IUsuarioRepository>();
        var controller = new UsuarioController(
            repository.Object,
            Mock.Of<ILocalizacaoCadastroService>(),
            Mock.Of<IEmailService>())
        {
            ControllerContext = CriarContexto(7, "PROFISSIONAL")
        };

        var result = await controller.AtualizarPerfil(8, new UpdatePerfilModel
        {
            Nome = "Outro usuario",
            Email = "outro@mail.com"
        });

        Assert.IsType<ForbidResult>(result);
        repository.Verify(item => item.AtualizarPerfilUsuario(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task PreCadastroPublicoDeProfissionalCriaVinculoPendente()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var emailService = new Mock<IEmailService>();
        repository
            .Setup(item => item.InserirUsuario(
                It.IsAny<UsuarioModel>(),
                7,
                "PENDENTE",
                It.IsAny<CoordenadasModel>()))
            .ReturnsAsync((UsuarioModel usuario, int? _, string _, CoordenadasModel? _) =>
            {
                usuario.Id = 15;
                return usuario;
            });
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            emailService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Senha = "senha-segura",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal("PENDENTE", created.Value!.GetType().GetProperty("VinculoStatus")!.GetValue(created.Value));
        repository.Verify(item => item.InserirUsuario(
            It.Is<UsuarioModel>(usuario => usuario.TipoUsuario == SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL),
            7,
            "PENDENTE",
            It.IsAny<CoordenadasModel>()), Times.Once);
        emailService.Verify(item => item.EnviarCredenciaisProfissionalAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EmpresaCriaProfissionalComVinculoAprovado()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var emailService = new Mock<IEmailService>();
        string? senhaPersistida = null;
        repository
            .Setup(item => item.InserirUsuario(
                It.IsAny<UsuarioModel>(),
                7,
                "APROVADO",
                It.IsAny<CoordenadasModel>()))
            .ReturnsAsync((UsuarioModel usuario, int? _, string _, CoordenadasModel? _) =>
            {
                senhaPersistida = usuario.Senha;
                usuario.Id = 15;
                return usuario;
            });
        emailService
            .Setup(item => item.EnviarCredenciaisProfissionalAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            emailService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        var created = Assert.IsType<CreatedAtActionResult>(result);
        Assert.NotNull(senhaPersistida);
        Assert.Equal(16, senhaPersistida.Length);
        Assert.Contains(senhaPersistida, char.IsLower);
        Assert.Contains(senhaPersistida, char.IsUpper);
        Assert.Contains(senhaPersistida, char.IsDigit);
        Assert.Contains(senhaPersistida, caractere => "!@$%*-_?".Contains(caractere));
        Assert.Equal(
            senhaPersistida,
            created.Value!.GetType().GetProperty("senhaTemporaria")!.GetValue(created.Value));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        repository.Verify(item => item.InserirUsuario(
            It.Is<UsuarioModel>(usuario => usuario.Senha == senhaPersistida),
            7,
            "APROVADO",
            It.IsAny<CoordenadasModel>()), Times.Once);
        emailService.Verify(item => item.EnviarCredenciaisProfissionalAsync(
            "profissional@example.com",
            "profissional@example.com",
            senhaPersistida,
            "Profissional"), Times.Never);
    }

    [Fact]
    public async Task EmpresaRepeteCadastroExistenteEReenviaNovoAcesso()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var emailService = new Mock<IEmailService>();
        var profissional = new UsuarioModel
        {
            Id = 15,
            Nome = "Profissional",
            Email = "profissional@example.com",
            Senha = "hash-existente",
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        };
        repository
            .Setup(item => item.ListarUsuarioPorEmail("profissional@example.com"))
            .ReturnsAsync(profissional);
        repository
            .Setup(item => item.ObterStatusVinculoProfissionalEmpresa(15, 7))
            .ReturnsAsync("APROVADO");
        repository
            .Setup(item => item.AtualizarAcessoProfissionalEmpresa(15, 7, It.IsAny<string>()))
            .ReturnsAsync(true);
        emailService
            .Setup(item => item.EnviarCredenciaisProfissionalAsync(
                "profissional@example.com",
                "profissional@example.com",
                It.IsAny<string>(),
                "Profissional"))
            .Returns(Task.CompletedTask);
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            emailService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        repository.Verify(item => item.AtualizarAcessoProfissionalEmpresa(
            15,
            7,
            It.Is<string>(senha => senha.Length == 16)), Times.Once);
        repository.Verify(item => item.InserirUsuario(
            It.IsAny<UsuarioModel>(),
            It.IsAny<int?>(),
            It.IsAny<string>(),
            It.IsAny<CoordenadasModel>()), Times.Never);
        emailService.Verify(item => item.EnviarCredenciaisProfissionalAsync(
            "profissional@example.com",
            "profissional@example.com",
            It.Is<string>(senha => senha.Length == 16),
            "Profissional"), Times.Never);
        repository.Verify(item => item.RestaurarAcessoProfissionalEmpresa(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
        Assert.Equal(
            16,
            ok.Value!.GetType().GetProperty("senhaTemporaria")!.GetValue(ok.Value)!.ToString()!.Length);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task EmpresaAtivaVinculoPendenteEEnviaNovoAcesso()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var emailService = new Mock<IEmailService>();
        repository
            .Setup(item => item.ListarUsuarioPorEmail("profissional@example.com"))
            .ReturnsAsync(new UsuarioModel
            {
                Id = 15,
                Nome = "Profissional",
                Email = "profissional@example.com",
                Senha = "hash-anterior",
                TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
            });
        repository
            .Setup(item => item.ObterStatusVinculoProfissionalEmpresa(15, 7))
            .ReturnsAsync("PENDENTE");
        repository
            .Setup(item => item.AtualizarAcessoProfissionalEmpresa(15, 7, It.IsAny<string>()))
            .ReturnsAsync(true);
        emailService
            .Setup(item => item.EnviarCredenciaisProfissionalAsync(
                "profissional@example.com",
                "profissional@example.com",
                It.IsAny<string>(),
                "Profissional"))
            .Returns(Task.CompletedTask);
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            emailService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        var ok = Assert.IsType<OkObjectResult>(result);
        repository.Verify(item => item.AtualizarAcessoProfissionalEmpresa(
            15,
            7,
            It.Is<string>(senha => senha.Length == 16)), Times.Once);
        emailService.Verify(item => item.EnviarCredenciaisProfissionalAsync(
            "profissional@example.com",
            "profissional@example.com",
            It.Is<string>(senha => senha.Length == 16),
            "Profissional"), Times.Never);
        Assert.Equal(
            16,
            ok.Value!.GetType().GetProperty("senhaTemporaria")!.GetValue(ok.Value)!.ToString()!.Length);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task EmpresaNaoRegeneraAcessoDeProfissionalDeOutraEmpresa()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        repository
            .Setup(item => item.ListarUsuarioPorEmail("profissional@example.com"))
            .ReturnsAsync(new UsuarioModel
            {
                Id = 15,
                Nome = "Profissional",
                Email = "profissional@example.com",
                Senha = "hash-existente",
                TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
            });
        repository
            .Setup(item => item.ObterStatusVinculoProfissionalEmpresa(15, 7))
            .ReturnsAsync((string?)null);
        var emailService = new Mock<IEmailService>();
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            emailService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        Assert.IsType<ConflictObjectResult>(result);
        repository.Verify(item => item.AtualizarAcessoProfissionalEmpresa(
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<string>()), Times.Never);
        emailService.Verify(item => item.EnviarCredenciaisProfissionalAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CadastroProfissionalNaoDependeDoSmtp()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        repository
            .Setup(item => item.InserirUsuario(
                It.IsAny<UsuarioModel>(),
                7,
                "APROVADO",
                It.IsAny<CoordenadasModel>()))
            .ReturnsAsync((UsuarioModel usuario, int? _, string _, CoordenadasModel? _) =>
            {
                usuario.Id = 15;
                return usuario;
            });
        var emailService = new Mock<IEmailService>();
        emailService
            .Setup(item => item.EnviarCredenciaisProfissionalAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ThrowsAsync(new EmailEnvioException(
                "Falha SMTP",
                new InvalidOperationException("credencial-secreta")));
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            emailService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        Assert.IsType<CreatedAtActionResult>(result);
        emailService.Verify(item => item.EnviarCredenciaisProfissionalAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReativacaoDoProfissionalNaoDependeDoSmtp()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var emailService = new Mock<IEmailService>();
        repository
            .Setup(item => item.ListarUsuarioPorEmail("profissional@example.com"))
            .ReturnsAsync(new UsuarioModel
            {
                Id = 15,
                Nome = "Profissional",
                Email = "profissional@example.com",
                Senha = "hash-anterior",
                TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
            });
        repository
            .Setup(item => item.ObterStatusVinculoProfissionalEmpresa(15, 7))
            .ReturnsAsync("PENDENTE");
        repository
            .Setup(item => item.AtualizarAcessoProfissionalEmpresa(15, 7, It.IsAny<string>()))
            .ReturnsAsync(true);
        repository
            .Setup(item => item.RestaurarAcessoProfissionalEmpresa(
                15,
                7,
                "hash-anterior",
                "PENDENTE"))
            .Returns(Task.CompletedTask);
        emailService
            .Setup(item => item.EnviarCredenciaisProfissionalAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ThrowsAsync(new EmailEnvioException(
                "Falha SMTP",
                new InvalidOperationException("indisponivel")));
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            emailService.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(item => item.RestaurarAcessoProfissionalEmpresa(
            15,
            7,
            "hash-anterior",
            "PENDENTE"), Times.Never);
        emailService.Verify(item => item.EnviarCredenciaisProfissionalAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CriarProfissionalRejeitaEmpresaDiferenteDoToken()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            Mock.Of<IEmailService>())
        {
            ControllerContext = CriarContexto(8, "EMPRESA")
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Profissional",
            Email = "profissional@example.com",
            Senha = "senha-segura",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            EmpresaId = 7,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.PROFISSIONAL
        });

        Assert.IsType<ForbidResult>(result);
        repository.Verify(
            item => item.InserirUsuario(
                It.IsAny<UsuarioModel>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<CoordenadasModel>()),
            Times.Never);
    }

    [Fact]
    public async Task EmpresaNaoRespondeSolicitacaoDeOutraEmpresa()
    {
        var repository = new Mock<IVinculoRepository>();
        var controller = new VinculosController(repository.Object)
        {
            ControllerContext = CriarContexto(8, "EMPRESA")
        };

        var result = await controller.ResponderSolicitacao(
            7,
            21,
            new RespostaSolicitacaoModel { Status = "APROVADO" });

        Assert.IsType<ForbidResult>(result);
        repository.Verify(
            item => item.ResponderSolicitacaoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task RespostaRepetidaDeSolicitacaoRetornaConflito()
    {
        var repository = new Mock<IVinculoRepository>();
        repository
            .Setup(item => item.ResponderSolicitacaoAsync(21, 7, "RECUSADO"))
            .ReturnsAsync(false);
        var controller = new VinculosController(repository.Object)
        {
            ControllerContext = CriarContexto(7, "EMPRESA")
        };

        var result = await controller.ResponderSolicitacao(
            7,
            21,
            new RespostaSolicitacaoModel { Status = "RECUSADO" });

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task UploadComConteudoInvalidoERejeitado()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            Mock.Of<IEmailService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        await using var conteudo = new MemoryStream("<html>nao-e-imagem</html>"u8.ToArray());
        var arquivo = new FormFile(conteudo, 0, conteudo.Length, "FotoPerfil", "foto.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Empresa",
            Email = "empresa@example.com",
            Senha = "senha-segura",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.EMPRESA,
            FotoPerfil = arquivo
        });

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(
            item => item.InserirUsuario(
                It.IsAny<UsuarioModel>(),
                It.IsAny<int?>(),
                It.IsAny<string>(),
                It.IsAny<CoordenadasModel>()),
            Times.Never);
    }

    [Fact]
    public async Task CadastroPersisteFotoSelecionadaSemSubstituirPeloPadrao()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var uploadService = new Mock<IArquivoUploadService>();
        uploadService
            .Setup(item => item.SalvarAsync(
                It.IsAny<byte[]>(),
                ".png",
                "image/png",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("uploads/perfil-real.png");
        repository
            .Setup(item => item.InserirUsuario(
                It.IsAny<UsuarioModel>(),
                null,
                "APROVADO",
                It.IsAny<CoordenadasModel>()))
            .ReturnsAsync((UsuarioModel usuario, int? _, string _, CoordenadasModel? _) =>
            {
                usuario.Id = 23;
                return usuario;
            });
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            Mock.Of<IEmailService>(),
            arquivoUploadService: uploadService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        var bytesImagem = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01
        };
        var conteudo = new MemoryStream(bytesImagem);
        var arquivo = new FormFile(conteudo, 0, conteudo.Length, "FotoPerfil", "perfil.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Autonomo",
            Email = "autonomo-foto@example.com",
            Senha = "senha-segura",
            Latitude = -19.0183m,
            Longitude = -40.5367m,
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.AUTONOMO,
            FotoPerfil = arquivo
        });

        Assert.IsType<CreatedAtActionResult>(result);
        uploadService.Verify(item => item.SalvarAsync(
            It.Is<byte[]>(bytes => bytes.SequenceEqual(bytesImagem)),
            ".png",
            "image/png",
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.InserirUsuario(
            It.Is<UsuarioModel>(usuario =>
                usuario.FotoPerfil == "uploads/perfil-real.png"),
            null,
            "APROVADO",
            It.IsAny<CoordenadasModel>()), Times.Once);
    }

    [Fact]
    public async Task CadastroSemCoordenadasERejeitado()
    {
        var localizacao = CriarLocalizacaoCadastroMock();
        var repository = new Mock<IUsuarioRepository>();
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            Mock.Of<IEmailService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Autonomo",
            Email = "autonomo@example.com",
            Senha = "senha-segura",
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.AUTONOMO
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Latitude e longitude", badRequest.Value?.ToString());
        localizacao.Verify(item => item.ResolverAutomaticamenteAsync(
            It.IsAny<CoordenadasModel>(),
            It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.InserirUsuario(
            It.IsAny<UsuarioModel>(),
            It.IsAny<int?>(),
            It.IsAny<string>(),
            It.IsAny<CoordenadasModel>()), Times.Never);
    }

    [Fact]
    public async Task CadastroAutomaticoUsaCidadeResolvidaESalvaCoordenadas()
    {
        var localizacao = new Mock<ILocalizacaoCadastroService>();
        localizacao
            .Setup(item => item.ResolverAutomaticamenteAsync(
                It.Is<CoordenadasModel>(c => c.Latitude == -19.0183m && c.Longitude == -40.5367m),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalizacaoResolvidaModel
            {
                PaisId = 1,
                PaisNome = "Brasil",
                EstadoId = 2,
                EstadoNome = "Espirito Santo",
                CidadeId = 3,
                CidadeNome = "Sao Gabriel da Palha"
            });
        var repository = new Mock<IUsuarioRepository>();
        repository
            .Setup(item => item.InserirUsuario(
                It.IsAny<UsuarioModel>(),
                null,
                "APROVADO",
                It.IsAny<CoordenadasModel>()))
            .ReturnsAsync((UsuarioModel usuario, int? _, string _, CoordenadasModel _) => usuario);
        var controller = new UsuarioController(
            repository.Object,
            localizacao.Object,
            Mock.Of<IEmailService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.InserirUsuario(new CreateUsuarioModel
        {
            Nome = "Autonomo",
            Email = "autonomo@example.com",
            Senha = "senha-segura",
            TipoUsuario = SaaS.Domain.Enums.TipoUsuario.AUTONOMO,
            Latitude = -19.0183m,
            Longitude = -40.5367m
        });

        Assert.IsType<CreatedAtActionResult>(result);
        repository.Verify(item => item.InserirUsuario(
            It.Is<UsuarioModel>(usuario => usuario.CidadeFk == 3),
            null,
            "APROVADO",
            It.Is<CoordenadasModel>(c => c.Latitude == -19.0183m && c.Longitude == -40.5367m)),
            Times.Once);
    }

    private static ControllerContext CriarContexto(int usuarioId, string papel)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()),
                new Claim(ClaimTypes.Role, papel)
            },
            "Test");

        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private static Mock<ILocalizacaoCadastroService> CriarLocalizacaoCadastroMock(int cidadeId = 1)
    {
        var service = new Mock<ILocalizacaoCadastroService>();
        service
            .Setup(item => item.ResolverAutomaticamenteAsync(
                It.IsAny<CoordenadasModel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalizacaoResolvidaModel
            {
                PaisId = 1,
                PaisNome = "Brasil",
                EstadoId = 2,
                EstadoNome = "Espirito Santo",
                CidadeId = cidadeId,
                CidadeNome = "Sao Gabriel da Palha"
            });
        return service;
    }
}
