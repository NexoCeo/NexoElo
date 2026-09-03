namespace SaaS.Domain.Models;

public sealed class RelatorioMensalModel
{
    public string ResponsavelNome { get; set; } = string.Empty;
    public string ResponsavelTipo { get; set; } = string.Empty;
    public DateTime PeriodoInicio { get; set; }
    public DateTime PeriodoFim { get; set; }
    public int TotalAgendamentos { get; set; }
    public int TotalAgendados { get; set; }
    public int TotalConcluidos { get; set; }
    public int TotalCancelados { get; set; }
    public decimal FaturamentoTotal { get; set; }
    public decimal TicketMedio { get; set; }
    public List<RelatorioServicoModel> Servicos { get; set; } = [];
    public List<RelatorioFaturamentoDiaModel> FaturamentoPorDia { get; set; } = [];
    public List<RelatorioAgendamentoItemModel> Agendamentos { get; set; } = [];
}

public sealed class RelatorioServicoModel
{
    public string Nome { get; set; } = string.Empty;
    public int QuantidadeConcluida { get; set; }
    public decimal Faturamento { get; set; }
}

public sealed class RelatorioFaturamentoDiaModel
{
    public DateTime Data { get; set; }
    public int QuantidadeConcluida { get; set; }
    public decimal Faturamento { get; set; }
}

public sealed class RelatorioAgendamentoItemModel
{
    public DateTime DataAgendamento { get; set; }
    public string ClienteNome { get; set; } = string.Empty;
    public string PrestadorNome { get; set; } = string.Empty;
    public string ServicoNome { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Valor { get; set; }
}

public sealed class RelatorioArquivoModel
{
    public string NomeArquivo { get; set; } = string.Empty;
    public byte[] Conteudo { get; set; } = [];
}
