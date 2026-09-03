using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SaaS.Application.Exceptions;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Services
{
    public sealed class NominatimGeocodingService : IGeocodingService, IDisposable
    {
        private static readonly IReadOnlyDictionary<string, string> PaisesPorCodigo =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BR"] = "Brasil",
                ["PT"] = "Portugal"
            };

        private static readonly IReadOnlyDictionary<string, string> EstadosBrasileirosPorCodigo =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AC"] = "Acre", ["AL"] = "Alagoas", ["AP"] = "Amapa",
                ["AM"] = "Amazonas", ["BA"] = "Bahia", ["CE"] = "Ceara",
                ["DF"] = "Distrito Federal", ["ES"] = "Espirito Santo",
                ["GO"] = "Goias", ["MA"] = "Maranhao", ["MT"] = "Mato Grosso",
                ["MS"] = "Mato Grosso do Sul", ["MG"] = "Minas Gerais",
                ["PA"] = "Para", ["PB"] = "Paraiba", ["PR"] = "Parana",
                ["PE"] = "Pernambuco", ["PI"] = "Piaui", ["RJ"] = "Rio de Janeiro",
                ["RN"] = "Rio Grande do Norte", ["RS"] = "Rio Grande do Sul",
                ["RO"] = "Rondonia", ["RR"] = "Roraima", ["SC"] = "Santa Catarina",
                ["SP"] = "Sao Paulo", ["SE"] = "Sergipe", ["TO"] = "Tocantins"
            };

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly TimeSpan _intervaloMinimo;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        private DateTimeOffset _ultimaRequisicao = DateTimeOffset.MinValue;
        private bool _disposeHttpClient;

        public NominatimGeocodingService(IConfiguration configuration)
            : this(
                CriarHttpClient(configuration),
                configuration["Geocoding:BaseUrl"] ?? "https://nominatim.openstreetmap.org",
                TimeSpan.FromMilliseconds(1000))
        {
            _disposeHttpClient = true;
        }

        public NominatimGeocodingService(
            HttpClient httpClient,
            string baseUrl,
            TimeSpan? intervaloMinimo = null)
        {
            _httpClient = httpClient;
            _baseUrl = baseUrl.TrimEnd('/');
            _intervaloMinimo = intervaloMinimo ?? TimeSpan.FromSeconds(1);
        }

        public async Task<EnderecoGeocodificadoModel?> ObterEnderecoAsync(
            CoordenadasModel coordenadas,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = string.Create(
                CultureInfo.InvariantCulture,
                $"{coordenadas.Latitude:F6},{coordenadas.Longitude:F6}");

            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiraEm > DateTimeOffset.UtcNow)
                return cached.Endereco;

            await _requestGate.WaitAsync(cancellationToken);
            try
            {
                var espera = _intervaloMinimo - (DateTimeOffset.UtcNow - _ultimaRequisicao);
                if (espera > TimeSpan.Zero)
                    await Task.Delay(espera, cancellationToken);

                var url = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_baseUrl}/reverse?format=jsonv2&addressdetails=1&accept-language=pt-BR&lat={coordenadas.Latitude}&lon={coordenadas.Longitude}");

                using var response = await _httpClient.GetAsync(url, cancellationToken);
                _ultimaRequisicao = DateTimeOffset.UtcNow;

                if (!response.IsSuccessStatusCode)
                {
                    throw new GeocodingIndisponivelException(
                        $"O servico de geolocalizacao respondeu com status {(int)response.StatusCode}.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var endereco = ExtrairEndereco(document.RootElement);

                if (endereco == null)
                    return null;

                _cache[cacheKey] = new CacheEntry(endereco, DateTimeOffset.UtcNow.AddHours(24));
                return endereco;
            }
            catch (GeocodingIndisponivelException)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GeocodingIndisponivelException(
                    "O servico de geolocalizacao excedeu o tempo limite.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new GeocodingIndisponivelException(
                    "Nao foi possivel consultar o servico de geolocalizacao.", ex);
            }
            finally
            {
                _requestGate.Release();
            }
        }

        public void Dispose()
        {
            _requestGate.Dispose();
            if (_disposeHttpClient)
                _httpClient.Dispose();
        }

        private static HttpClient CriarHttpClient(IConfiguration configuration)
        {
            var userAgent = configuration["Geocoding:UserAgent"];
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                throw new InvalidOperationException(
                    "Configure Geocoding:UserAgent com o nome da aplicacao e um contato valido.");
            }

            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent.Trim());
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        private static EnderecoGeocodificadoModel? ExtrairEndereco(JsonElement root)
        {
            if (!root.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object)
                return null;

            var pais = ObterTexto(address, "country");
            var estado = ObterTexto(address, "state");
            var paisCodigo = ObterTexto(address, "country_code")?.ToUpperInvariant();
            var estadoCodigo = ObterTexto(address, "ISO3166-2-lvl4")?.ToUpperInvariant();

            var paises = NovosCandidatos(pais);
            if (paisCodigo != null && PaisesPorCodigo.TryGetValue(paisCodigo, out var paisPorCodigo))
                AdicionarCandidato(paises, paisPorCodigo);

            var estados = NovosCandidatos(estado);
            var siglaEstado = estadoCodigo?.Split('-').LastOrDefault();
            if (paisCodigo == "BR" && siglaEstado != null &&
                EstadosBrasileirosPorCodigo.TryGetValue(siglaEstado, out var estadoPorCodigo))
            {
                AdicionarCandidato(estados, estadoPorCodigo);
            }

            var cidades = new List<string>();
            foreach (var key in new[] { "city", "town", "village", "municipality", "county" })
                AdicionarCandidato(cidades, ObterTexto(address, key));

            if (paises.Count == 0 || estados.Count == 0 || cidades.Count == 0)
                return null;

            return new EnderecoGeocodificadoModel
            {
                Paises = paises,
                Estados = estados,
                Cidades = cidades
            };
        }

        private static List<string> NovosCandidatos(string? value)
        {
            var values = new List<string>();
            AdicionarCandidato(values, value);
            return values;
        }

        private static void AdicionarCandidato(List<string> values, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !values.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                values.Add(value.Trim());
            }
        }

        private static string? ObterTexto(JsonElement element, string property)
        {
            return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private sealed record CacheEntry(
            EnderecoGeocodificadoModel Endereco,
            DateTimeOffset ExpiraEm);
    }
}
