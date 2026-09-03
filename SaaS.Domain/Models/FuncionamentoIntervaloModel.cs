using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    public class FuncionamentoIntervaloModel
    {
        public DiaFuncionamento DiaFuncionamento { get; set; }
        public string? HoraInicio { get; set; }
        public string? HoraFim { get; set; }
        public string? HoraEntrada { get; set; }
        public string? HoraSaida { get; set; }
    }
}
