namespace SaaS.Domain.Models
{
    public class CancelarAssinaturaModel
    {
        public bool CancelarAoFimDoPeriodo { get; set; } = true;
        public bool GerarFaturaFinal { get; set; }
        public bool AplicarProporcional { get; set; }
    }
}

