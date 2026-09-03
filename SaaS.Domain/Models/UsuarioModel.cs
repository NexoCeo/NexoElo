using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SaaS.Domain.Enums;

namespace SaaS.Domain.Models
{
    [Table("USUARIOS")]
    public class UsuarioModel
    {
        [Key]
        [Column("ID_USUARIO")]
        public int Id { get; set; }

        [Column("NOME_USUARIO")]
        public string Nome { get; set; }

        [Column("EMAIL_USUARIO")]
        public string? Email { get; set; }

        [Column("TELEFONE_USUARIO")]
        public string? Telefone { get; set; }

        [Column("SLUG_USUARIO")]
        public string? Slug { get; set; }

        [Column("SENHA_USUARIO")]
        public string Senha { get; set; }

        [Column("FOTO_PERFIL")]
        public string? FotoPerfil { get; set; }

        [Column("TIPO_USUARIO")]
        public TipoUsuario TipoUsuario { get; set; }

        [Column("ASSINATURA_ATIVA")]
        public StatusAssinatura? AssinaturaAtiva { get; set; }

        [Column("DATA_FIM_ASSINATURA")]
        public DateTime? DataFimAssinatura { get; set; }

        [Column("DATA_CRIACAO_USUARIO")]
        public DateTime DataCriacao { get; set; }

        [Column("DATA_ALTERACAO_USUARIO")]
        public DateTime DataAlteracao { get; set; }

        [Column("CIDADE_FK")]
        public int? CidadeFk { get; set; }

        [Column("STATUS_SOLICITACAO")]
        public string? VinculoStatus { get; set; }
    }
}
