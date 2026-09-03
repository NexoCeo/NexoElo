namespace SaaS.Domain.Exceptions
{
    public sealed class AgendamentoIndisponivelException : Exception
    {
        public DateTime? SugestaoDataAgendamento { get; }

        public AgendamentoIndisponivelException(
            string message,
            DateTime? sugestaoDataAgendamento = null)
            : base(message)
        {
            SugestaoDataAgendamento = sugestaoDataAgendamento;
        }
    }
}
