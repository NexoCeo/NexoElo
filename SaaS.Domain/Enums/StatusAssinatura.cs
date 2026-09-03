namespace SaaS.Domain.Enums
{
    public enum StatusAssinatura
    {
        ATIVA,
        NAO_ATIVA,
        EXPIRADO,
        CANCELADA,
        PENDENTE,
        CANCELAMENTO_PENDENTE
    }

    public static class StatusAssinaturaExtensions
    {
        public static string ToDatabaseValue(this StatusAssinatura statusAssinatura)
        {
            return statusAssinatura.ToString();
        }

        public static StatusAssinatura FromDatabaseValue(string? value)
        {
            if (Enum.TryParse<StatusAssinatura>(value, true, out var statusAssinatura) &&
                Enum.IsDefined(typeof(StatusAssinatura), statusAssinatura))
            {
                return statusAssinatura;
            }

            return StatusAssinatura.NAO_ATIVA;
        }
    }
}
