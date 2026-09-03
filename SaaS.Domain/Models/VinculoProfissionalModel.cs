using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SaaS.Domain.Models
{
    public class VinculoProfissionalModel
    {
        public int Id { get; set; }

        public int ProfissionalId { get; set; }

        public int EmpresaId { get; set; }

        public string? EmpresaNome { get; set; }

        public string VinculoStatus { get; set; } = string.Empty;

        public DateTime? DataSolicitacao { get; set; }
    }
}
