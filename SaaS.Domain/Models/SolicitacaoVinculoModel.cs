using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SaaS.Domain.Models
{
    public class SolicitacaoVinculoModel
    {
        public int Id { get; set; }

        public int ProfissionalId { get; set; }

        public string? ProfissionalNome { get; set; }

        public string? ProfissionalEmail { get; set; }

        public string? ProfissionalTelefone { get; set; }

        public string VinculoStatus { get; set; } = string.Empty;

        public DateTime? DataSolicitacao { get; set; }
    }
}
