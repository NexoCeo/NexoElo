using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Identity;
using SaaS.Domain.Models;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Infrastructure.Persistence.Repositories;
using SaaS.Infrastructure.Services;

namespace SaaS.Infrastructure
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddInfrastructure(this IServiceCollection services)
        {
            services.AddScoped<IPasswordHasher<UsuarioModel>, PasswordHasher<UsuarioModel>>();
            services.AddScoped<IUsuarioRepository, UsuarioRepository>();
            services.AddScoped<IAgendamentoRepository, AgendamentoRepository>();
            services.AddScoped<IVinculoRepository, VinculoRepository>();
            services.AddScoped<IAssinaturaRepository, AssinaturaRepository>();
            services.AddScoped<IForgotPasswordRepository, ForgotPasswordRepository>();
            services.AddScoped<IRecuperacaoSenhaRepository, RecuperacaoSenhaRepository>();
            services.AddScoped<IServicoRepository, ServicoRepository>();
            services.AddScoped<ILocalizacaoRepository, LocalizacaoRepository>();
            services.AddSingleton<IGeocodingService, NominatimGeocodingService>();
            services.AddScoped<ILocalizacaoCadastroService, LocalizacaoCadastroService>();
            services.AddScoped<IArquivoUploadService, PostgresArquivoUploadService>();
            services.AddScoped<IProfissionalRepository, ProfissionalRepository>();
            services.AddScoped<IFuncionamentoRepository, FuncionamentoRepository>();
            services.AddScoped<IRelatorioRepository, RelatorioRepository>();
            services.AddSingleton<ISmtpClientFactory, SmtpClientFactory>();
            services.AddScoped<IEmailService, EmailService>();
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IRelatorioService, RelatorioService>();
            services.AddScoped<IStripePaymentService, StripePaymentService>();

            return services;
        }
    }
}
