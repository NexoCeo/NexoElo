using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SaaS.Api.Controllers;
using SaaS.Application.Exceptions;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;
using SaaS.Infrastructure.Services;
using Xunit;

namespace SaaS.Api.Tests;

public class GeocodingServiceTests
{
    [Fact]
    public async Task ControllerRetornaCodigoEMensagemQuandoLocalizacaoNaoForResolvida()
    {
        var service = new Mock<ILocalizacaoCadastroService>();
        service
            .Setup(item => item.ResolverAutomaticamenteAsync(
                It.IsAny<CoordenadasModel>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException(
                "A cidade identificada nao existe no cadastro de regioes."));
        var controller = new LocalizacaoController(
            Mock.Of<ILocalizacaoRepository>(),
            service.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var response = await controller.ResolverCoordenadas(new CoordenadasModel
        {
            Latitude = 32.210374m,
            Longitude = -89.953381m
        });

        var result = Assert.IsType<UnprocessableEntityObjectResult>(response);
        var body = JsonSerializer.Serialize(result.Value);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Contains("LOCALIZACAO_NAO_RESOLVIDA", body);
        Assert.Contains("A cidade identificada nao existe no cadastro de regioes.", body);
    }

    [Fact]
    public async Task NominatimResolveEnderecoECriaCachePorCoordenada()
    {
        const string json = """
            {
              "address": {
                "country": "Brasil",
                "country_code": "br",
                "state": "Esp\u00edrito Santo",
                "ISO3166-2-lvl4": "BR-ES",
                "town": "S\u00e3o Gabriel da Palha"
              }
            }
            """;
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        using var service = new NominatimGeocodingService(
            httpClient,
            "https://geocoding.example.test",
            TimeSpan.Zero);
        var coordenadas = new CoordenadasModel
        {
            Latitude = -19.0183m,
            Longitude = -40.5367m
        };

        var primeiro = await service.ObterEnderecoAsync(coordenadas);
        var segundo = await service.ObterEnderecoAsync(coordenadas);

        Assert.NotNull(primeiro);
        Assert.Contains("Brasil", primeiro.Paises);
        Assert.Contains("Espirito Santo", primeiro.Estados);
        Assert.Contains("S\u00e3o Gabriel da Palha", primeiro.Cidades);
        Assert.Same(primeiro, segundo);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task NominatimConverteFalhaDeRedeEmErroDeDisponibilidade()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        using var httpClient = new HttpClient(handler);
        using var service = new NominatimGeocodingService(
            httpClient,
            "https://geocoding.example.test",
            TimeSpan.Zero);

        await Assert.ThrowsAsync<GeocodingIndisponivelException>(() =>
            service.ObterEnderecoAsync(new CoordenadasModel { Latitude = 0, Longitude = 0 }));
    }

    [Theory]
    [InlineData("Esp\u00edrito Santo", "ESPIRITO SANTO")]
    [InlineData("s\u00e3o gabriel da palha", "S\u00c3O GABRIEL DA PALHA")]
    [InlineData("Munic\u00edpio de S\u00e3o Paulo", "sao paulo")]
    public void NormalizacaoIgnoraCaixaAcentosEPrefixos(string nomeBanco, string retornoExterno)
    {
        Assert.True(LocalizacaoNomeNormalizer.Equivale(nomeBanco, [retornoExterno]));
    }

    [Fact]
    public async Task LocalizacaoCadastroRejeitaLatitudeInvalidaAntesDoGeocoding()
    {
        var geocoding = new Mock<IGeocodingService>();
        var repository = new Mock<ILocalizacaoRepository>();
        var service = new LocalizacaoCadastroService(geocoding.Object, repository.Object);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolverAutomaticamenteAsync(
                new CoordenadasModel { Latitude = 90.01m, Longitude = 0 }));

        Assert.Contains("Latitude", error.Message);
        geocoding.Verify(
            item => item.ObterEnderecoAsync(It.IsAny<CoordenadasModel>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task LocalizacaoCadastroRejeitaLongitudeInvalidaAntesDoGeocoding()
    {
        var service = new LocalizacaoCadastroService(
            Mock.Of<IGeocodingService>(),
            Mock.Of<ILocalizacaoRepository>());

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolverAutomaticamenteAsync(
                new CoordenadasModel { Latitude = 0, Longitude = -180.01m }));

        Assert.Contains("Longitude", error.Message);
    }

    [Fact]
    public async Task LocalizacaoCadastroExigeCorrespondenciaHierarquicaNoBanco()
    {
        var geocoding = new Mock<IGeocodingService>();
        geocoding
            .Setup(item => item.ObterEnderecoAsync(
                It.IsAny<CoordenadasModel>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnderecoGeocodificadoModel
            {
                Paises = ["Brasil"],
                Estados = ["Espirito Santo"],
                Cidades = ["Cidade inexistente"]
            });
        var repository = new Mock<ILocalizacaoRepository>();
        repository
            .Setup(item => item.ResolverLocalizacaoAdministrativa(It.IsAny<EnderecoGeocodificadoModel>()))
            .ReturnsAsync((LocalizacaoResolvidaModel?)null);
        var service = new LocalizacaoCadastroService(geocoding.Object, repository.Object);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolverAutomaticamenteAsync(
                new CoordenadasModel { Latitude = -19, Longitude = -40 }));

        Assert.Contains("nao existe", error.Message);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_handler(request));
        }
    }
}
