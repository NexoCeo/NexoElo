using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    public class FuncionamentoConfigModel
    {
        public int UsuarioFk { get; set; }
        public List<FuncionamentoIntervaloModel> Intervalos { get; set; } = new();
        public List<DiaFuncionamento> DiasFuncionamento { get; set; } = new();
        public string? HoraInicio { get; set; } = "08:00";
        public string? HoraFim { get; set; } = "18:00";
        public int? LimiteDiario { get; set; }
        public int? LimiteSemanal { get; set; }
    }
}
