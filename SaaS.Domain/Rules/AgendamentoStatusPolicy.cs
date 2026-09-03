using SaaS.Domain.Enums;
using SaaS.Domain.Exceptions;

namespace SaaS.Domain.Rules
{
    public static class AgendamentoStatusPolicy
    {
        public const int LimiteCancelamentoClienteHoras = 2;

        public static string ValidarTransicao(
            string statusAtual,
            string statusDestino,
            TipoUsuario solicitante,
            DateTime dataAgendamento,
            DateTime agora)
        {
            var atual = NormalizarStatus(statusAtual);
            var destino = NormalizarStatus(statusDestino);

            if (destino is not ("CANCELADO" or "CONCLUIDO"))
                throw new ArgumentException("O status deve ser CANCELADO ou CONCLUIDO.");

            if (atual == destino)
                return destino;

            if (atual != "AGENDADO")
                throw new RegraAgendamentoException("Somente agendamentos com status AGENDADO podem ser alterados.");

            if (solicitante == TipoUsuario.CLIENTE &&
                destino == "CANCELADO" &&
                agora > dataAgendamento.AddHours(-LimiteCancelamentoClienteHoras))
            {
                throw new RegraAgendamentoException(
                    "Limite de prazo atingido, o agendamento não pode ser cancelado.");
            }

            return destino;
        }

        private static string NormalizarStatus(string? status)
        {
            return status?.Trim().ToUpperInvariant() ?? string.Empty;
        }
    }
}
