using System.Text.Encodings.Web;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using SaaS.Application.Exceptions;
using SaaS.Application.Interfaces.Services;

namespace SaaS.Infrastructure.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _config;
        private readonly ISmtpClientFactory _smtpClientFactory;

        public EmailService(
            IConfiguration config,
            ISmtpClientFactory smtpClientFactory)
        {
            _config = config;
            _smtpClientFactory = smtpClientFactory;
        }

        public Task EnviarCodigoAsync(string destino, string codigo, string nomeUsuario)
        {
            var nomeSeguro = HtmlEncoder.Default.Encode(nomeUsuario);
            var codigoSeguro = HtmlEncoder.Default.Encode(codigo);
            var corpo = $@"
                <html>
                  <body style=""font-family: Arial, sans-serif; padding: 20px; background-color: #f4f6fa; color: #333;"">
                    <div style=""max-width: 600px; margin: auto; background-color: #ffffff; padding: 30px; border-radius: 10px; box-shadow: 0 4px 10px rgba(0,0,0,0.05);"">
                      <h2 style=""color: #4169E1; font-size: 24px; margin-bottom: 20px;"">Recuperação de Senha</h2>
                      <p style=""font-size: 16px;"">Olá, {nomeSeguro}!</p>
                      <p style=""font-size: 16px;"">Você solicitou a recuperação de sua senha. Aqui está seu código:</p>
                      <h1 style=""color: #ffffff; background-color: #4169E1; padding: 15px 30px; display: inline-block; border-radius: 8px; letter-spacing: 2px; font-size: 28px;"">{codigoSeguro}</h1>
                      <p style=""font-size: 14px; margin-top: 20px;"">Este código é válido por <strong>10 minutos</strong>.</p>
                      <p style=""font-size: 14px;"">Se você não solicitou isso, apenas ignore este e-mail.</p>
                      <hr style=""margin: 30px 0; border: none; border-top: 1px solid #ddd;"" />
                      <p style=""font-size: 14px; display: flex; align-items: center;"">
                        <img src=""https://raw.githubusercontent.com/joaogentelucio/teste-app/master/src/assets/logo.png"" alt=""Logo"" style=""height: 24px; margin-right: 8px; vertical-align: middle;"">
                        <strong>Equipe App</strong>
                      </p>
                    </div>
                  </body>
                </html>";

            return EnviarAsync(
                destino,
                "Código de recuperação de senha",
                corpo);
        }

        public Task EnviarCredenciaisProfissionalAsync(
            string destino,
            string emailProfissional,
            string senhaTemporaria,
            string nomeProfissional)
        {
            var nomeSeguro = HtmlEncoder.Default.Encode(nomeProfissional);
            var emailSeguro = HtmlEncoder.Default.Encode(emailProfissional);
            var senhaSegura = HtmlEncoder.Default.Encode(senhaTemporaria);
            var corpo = $@"
                <html>
                  <body style=""font-family: Arial, sans-serif; padding: 20px; background-color: #f4f6fa; color: #111827;"">
                    <div style=""max-width: 600px; margin: auto; background-color: #ffffff; padding: 30px; border-radius: 10px; border: 1px solid #d9dee7;"">
                      <h2 style=""color: #2563eb; font-size: 24px; margin-bottom: 20px;"">Seu acesso profissional</h2>
                      <p style=""font-size: 16px;"">Ola, {nomeSeguro}!</p>
                      <p style=""font-size: 16px;"">Uma empresa criou seu cadastro profissional no NexoCEO.</p>
                      <div style=""margin: 24px 0; padding: 20px; background-color: #f7f8fa; border: 1px solid #d9dee7; border-radius: 8px;"">
                        <p style=""margin: 0 0 12px;""><strong>E-mail:</strong> {emailSeguro}</p>
                        <p style=""margin: 0;""><strong>Senha temporaria:</strong> {senhaSegura}</p>
                      </div>
                      <p style=""font-size: 14px;"">Use essas credenciais para acessar o sistema. Por seguranca, altere sua senha apos o primeiro acesso.</p>
                      <hr style=""margin: 30px 0; border: none; border-top: 1px solid #ddd;"" />
                      <p style=""font-size: 14px;""><strong>Equipe NexoCEO</strong></p>
                    </div>
                  </body>
                </html>";

            return EnviarAsync(destino, "Seu acesso profissional ao NexoCEO", corpo);
        }

        private async Task EnviarAsync(string destino, string assunto, string corpoHtml)
        {
            try
            {
                var remetente = ObterConfiguracao("Email:From");
                var nomeRemetente = ObterConfiguracao("Email:Name");
                var smtpHost = ObterConfiguracao("Email:SmtpHost");
                var smtpPort = ObterPortaSmtp();
                var smtpUsuario = ObterConfiguracao("Email:Username");
                var smtpCredencial = ObterConfiguracao("Email:Password");

                var mensagem = new MimeMessage();
                mensagem.From.Add(new MailboxAddress(nomeRemetente, remetente));
                mensagem.To.Add(MailboxAddress.Parse(destino));
                mensagem.Subject = assunto;
                mensagem.Body = new TextPart("html") { Text = corpoHtml };

                using var client = _smtpClientFactory.Create();
                const int timeoutMilliseconds = 15_000;
                client.Timeout = timeoutMilliseconds;
                using var timeout = new CancellationTokenSource(timeoutMilliseconds);

                await client.ConnectAsync(
                    smtpHost,
                    smtpPort,
                    SecureSocketOptions.StartTls,
                    timeout.Token);
                await client.AuthenticateAsync(
                    smtpUsuario,
                    smtpCredencial,
                    timeout.Token);
                await client.SendAsync(mensagem, timeout.Token);
                await client.DisconnectAsync(true, timeout.Token);
            }
            catch (OperationCanceledException ex)
            {
                throw new EmailEnvioException(
                    "smtp_timeout",
                    "O servidor SMTP nao respondeu dentro do tempo limite.",
                    ex);
            }
            catch (MailKit.Security.AuthenticationException ex)
            {
                throw new EmailEnvioException(
                    "smtp_authentication",
                    "O servidor SMTP recusou as credenciais configuradas.",
                    ex);
            }
            catch (SmtpCommandException ex)
            {
                throw new EmailEnvioException(
                    ObterCodigoComandoSmtp(ex),
                    "O servidor SMTP recusou o envio da mensagem.",
                    ex);
            }
            catch (SmtpProtocolException ex)
            {
                throw new EmailEnvioException(
                    "smtp_protocol",
                    "A comunicacao com o servidor SMTP falhou.",
                    ex);
            }
            catch (SocketException ex)
            {
                throw new EmailEnvioException(
                    "smtp_connection",
                    "Nao foi possivel conectar ao servidor SMTP.",
                    ex);
            }
            catch (IOException ex)
            {
                throw new EmailEnvioException(
                    "smtp_connection",
                    "A conexao com o servidor SMTP foi interrompida.",
                    ex);
            }
            catch (InvalidOperationException ex)
            {
                throw new EmailEnvioException(
                    "smtp_configuration",
                    "A configuracao do servidor SMTP e invalida.",
                    ex);
            }
            catch (Exception ex)
            {
                throw new EmailEnvioException(
                    "smtp_unknown",
                    "Nao foi possivel enviar o email pelo servidor SMTP configurado.",
                    ex);
            }
        }

        private static string ObterCodigoComandoSmtp(SmtpCommandException exception)
        {
            return exception.ErrorCode switch
            {
                SmtpErrorCode.SenderNotAccepted => "smtp_sender_rejected",
                SmtpErrorCode.RecipientNotAccepted => "smtp_recipient_rejected",
                SmtpErrorCode.MessageNotAccepted => "smtp_message_rejected",
                _ => "smtp_command"
            };
        }

        private int ObterPortaSmtp()
        {
            var valor = ObterConfiguracao("Email:SmtpPort");
            return int.TryParse(valor, out var porta) && porta is > 0 and <= 65535
                ? porta
                : throw new InvalidOperationException(
                    "Configuracao invalida: Email:SmtpPort deve ser uma porta valida.");
        }

        private string ObterConfiguracao(string chave)
        {
            return ObterConfiguracaoOpcional(chave)
                ?? throw new InvalidOperationException($"Configuracao obrigatoria ausente: {chave}.");
        }

        private string? ObterConfiguracaoOpcional(string chave)
        {
            var valor = _config[chave];
            return string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        }
    }
}
