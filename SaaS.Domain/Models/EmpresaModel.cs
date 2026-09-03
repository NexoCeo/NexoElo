using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SaaS.Domain.Models
{
    [Table("EMPRESAS")]
    public class EmpresaModel : UsuarioModel
    {
        [Column("NOME_FANTASIA")]
        public string? NomeFantasia { get; set; }

        [Column("CNPJ_EMPRESA")]
        public string? Cnpj { get; set; }

        [Column("USUARIO_FK")]
        public int UsuarioId { get; set; }
    }
}

