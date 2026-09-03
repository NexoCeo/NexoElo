using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SaaS.Application.Interfaces.Repositories;
using SaaS.Application.Interfaces.Services;
using SaaS.Domain.Enums;
using SaaS.Domain.Models;

namespace SaaS.Infrastructure.Services;

public sealed class RelatorioService : IRelatorioService
{
    private static readonly CultureInfo CulturaBrasil = CultureInfo.GetCultureInfo("pt-BR");
    private const string CorPrimaria = "#315FCC";
    private const string CorTexto = "#202735";
    private const string CorTextoSecundario = "#667085";
    private const string CorBorda = "#D8DEE9";
    private const string CorFundoSuave = "#F4F7FC";

    private readonly IRelatorioRepository _relatorioRepository;

    static RelatorioService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public RelatorioService(IRelatorioRepository relatorioRepository)
    {
        _relatorioRepository = relatorioRepository;
    }

    public async Task<RelatorioArquivoModel> GerarRelatorioAsync(
        int usuarioId,
        TipoRelatorio tipo,
        int ano,
        int mes)
    {
        var dados = await _relatorioRepository.ObterRelatorioMensalAsync(
            usuarioId,
            ano,
            mes);

        var conteudo = Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(style => style
                    .FontFamily(Fonts.Lato)
                    .FontSize(9)
                    .FontColor(CorTexto));

                page.Header().Element(container => ComporCabecalho(container, dados, tipo));
                page.Content().PaddingVertical(18).Element(container =>
                    ComporConteudo(container, dados, tipo));
                page.Footer().Element(ComporRodape);
            });
        }).GeneratePdf();

        return new RelatorioArquivoModel
        {
            NomeArquivo = $"{tipo.ToFileName()}-{ano:D4}-{mes:D2}.pdf",
            Conteudo = conteudo
        };
    }

    private static void ComporCabecalho(
        IContainer container,
        RelatorioMensalModel dados,
        TipoRelatorio tipo)
    {
        container.Column(column =>
        {
            column.Spacing(5);
            column.Item().Text(ObterTitulo(tipo))
                .FontSize(22)
                .SemiBold()
                .FontColor(CorPrimaria);
            column.Item().Text(dados.ResponsavelNome)
                .FontSize(12)
                .SemiBold();
            column.Item().Text(
                    $"Periodo: {dados.PeriodoInicio:dd/MM/yyyy} a {dados.PeriodoFim:dd/MM/yyyy}")
                .FontColor(CorTextoSecundario);
            column.Item().PaddingTop(7).LineHorizontal(1).LineColor(CorBorda);
        });
    }

    private static void ComporConteudo(
        IContainer container,
        RelatorioMensalModel dados,
        TipoRelatorio tipo)
    {
        container.Column(column =>
        {
            column.Spacing(16);
            column.Item().Text(ObterDescricao(tipo)).FontColor(CorTextoSecundario);

            switch (tipo)
            {
                case TipoRelatorio.RESUMO_FINANCEIRO:
                    ComporResumoFinanceiro(column, dados);
                    break;
                case TipoRelatorio.SERVICOS_MAIS_REALIZADOS:
                    ComporServicosMaisRealizados(column, dados);
                    break;
                case TipoRelatorio.AGENDA_MENSAL:
                    ComporAgendaMensal(column, dados);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(tipo));
            }
        });
    }

    private static void ComporResumoFinanceiro(ColumnDescriptor column, RelatorioMensalModel dados)
    {
        column.Item().Element(container => ComporIndicadores(container,
        [
            ("Faturamento", FormatarMoeda(dados.FaturamentoTotal)),
            ("Concluidos", dados.TotalConcluidos.ToString(CulturaBrasil)),
            ("Ticket medio", FormatarMoeda(dados.TicketMedio)),
            ("Cancelados", dados.TotalCancelados.ToString(CulturaBrasil))
        ]));

        column.Item().Text("Faturamento por dia").FontSize(13).SemiBold();

        if (dados.FaturamentoPorDia.Count == 0)
        {
            column.Item().Element(container => ComporEstadoVazio(
                container,
                "Nenhum atendimento concluido gerou faturamento neste periodo."));
            return;
        }

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
            });

            ComporCabecalhoTabela(table, "Data", "Atendimentos", "Faturamento");

            foreach (var item in dados.FaturamentoPorDia)
            {
                table.Cell().Element(CelulaTabela).Text(item.Data.ToString("dd/MM/yyyy"));
                table.Cell().Element(CelulaTabela).Text(
                    item.QuantidadeConcluida.ToString(CulturaBrasil));
                table.Cell().Element(CelulaTabela).Text(FormatarMoeda(item.Faturamento));
            }
        });
    }

    private static void ComporServicosMaisRealizados(
        ColumnDescriptor column,
        RelatorioMensalModel dados)
    {
        var maisRealizado = dados.Servicos.FirstOrDefault();

        column.Item().Element(container => ComporIndicadores(container,
        [
            ("Mais realizado", maisRealizado?.Nome ?? "Sem dados"),
            ("Execucoes", dados.TotalConcluidos.ToString(CulturaBrasil)),
            ("Servicos ativos", dados.Servicos.Count.ToString(CulturaBrasil)),
            ("Faturamento", FormatarMoeda(dados.FaturamentoTotal))
        ]));

        column.Item().Text("Ranking de servicos concluidos").FontSize(13).SemiBold();

        if (dados.Servicos.Count == 0)
        {
            column.Item().Element(container => ComporEstadoVazio(
                container,
                "Nenhum servico foi concluido neste periodo."));
            return;
        }

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(28);
                columns.RelativeColumn(4);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
            });

            ComporCabecalhoTabela(
                table,
                "#",
                "Servico",
                "Execucoes",
                "Participacao",
                "Faturamento");

            for (var index = 0; index < dados.Servicos.Count; index++)
            {
                var servico = dados.Servicos[index];
                var participacao = dados.TotalConcluidos > 0
                    ? (decimal)servico.QuantidadeConcluida / dados.TotalConcluidos
                    : 0;

                table.Cell().Element(CelulaTabela).Text((index + 1).ToString(CulturaBrasil));
                table.Cell().Element(CelulaTabela).Text(servico.Nome);
                table.Cell().Element(CelulaTabela).Text(
                    servico.QuantidadeConcluida.ToString(CulturaBrasil));
                table.Cell().Element(CelulaTabela).Text(
                    participacao.ToString("P1", CulturaBrasil));
                table.Cell().Element(CelulaTabela).Text(FormatarMoeda(servico.Faturamento));
            }
        });
    }

    private static void ComporAgendaMensal(ColumnDescriptor column, RelatorioMensalModel dados)
    {
        column.Item().Element(container => ComporIndicadores(container,
        [
            ("Total", dados.TotalAgendamentos.ToString(CulturaBrasil)),
            ("Agendados", dados.TotalAgendados.ToString(CulturaBrasil)),
            ("Concluidos", dados.TotalConcluidos.ToString(CulturaBrasil)),
            ("Cancelados", dados.TotalCancelados.ToString(CulturaBrasil))
        ]));

        column.Item().Text("Agendamentos do periodo").FontSize(13).SemiBold();

        if (dados.Agendamentos.Count == 0)
        {
            column.Item().Element(container => ComporEstadoVazio(
                container,
                "Nenhum agendamento foi encontrado neste periodo."));
            return;
        }

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(2);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(1.5f);
            });

            ComporCabecalhoTabela(
                table,
                "Data e hora",
                "Cliente",
                "Prestador",
                "Servico",
                "Status",
                "Valor");

            foreach (var item in dados.Agendamentos)
            {
                table.Cell().Element(CelulaTabela).Text(
                    item.DataAgendamento.ToString("dd/MM HH:mm"));
                table.Cell().Element(CelulaTabela).Text(item.ClienteNome);
                table.Cell().Element(CelulaTabela).Text(item.PrestadorNome);
                table.Cell().Element(CelulaTabela).Text(item.ServicoNome);
                table.Cell().Element(CelulaTabela).Text(FormatarStatus(item.Status));
                table.Cell().Element(CelulaTabela).Text(FormatarMoeda(item.Valor));
            }
        });
    }

    private static void ComporIndicadores(
        IContainer container,
        IReadOnlyList<(string Rotulo, string Valor)> indicadores)
    {
        container.Row(row =>
        {
            row.Spacing(8);

            foreach (var indicador in indicadores)
            {
                row.RelativeItem()
                    .Border(1)
                    .BorderColor(CorBorda)
                    .Background(CorFundoSuave)
                    .Padding(10)
                    .Column(column =>
                    {
                        column.Spacing(4);
                        column.Item().Text(indicador.Rotulo)
                            .FontSize(8)
                            .FontColor(CorTextoSecundario);
                        column.Item().Text(indicador.Valor)
                            .FontSize(11)
                            .SemiBold()
                            .FontColor(CorPrimaria);
                    });
            }
        });
    }

    private static void ComporCabecalhoTabela(TableDescriptor table, params string[] titulos)
    {
        table.Header(header =>
        {
            foreach (var titulo in titulos)
            {
                header.Cell()
                    .Background(CorPrimaria)
                    .PaddingVertical(7)
                    .PaddingHorizontal(6)
                    .Text(titulo)
                    .FontSize(8)
                    .SemiBold()
                    .FontColor(Colors.White);
            }
        });
    }

    private static IContainer CelulaTabela(IContainer container)
    {
        return container
            .BorderBottom(1)
            .BorderColor(CorBorda)
            .PaddingVertical(6)
            .PaddingHorizontal(6);
    }

    private static void ComporEstadoVazio(IContainer container, string mensagem)
    {
        container
            .Border(1)
            .BorderColor(CorBorda)
            .Background(CorFundoSuave)
            .Padding(18)
            .AlignCenter()
            .Text(mensagem)
            .FontColor(CorTextoSecundario);
    }

    private static void ComporRodape(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Text($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm}")
                .FontSize(8)
                .FontColor(CorTextoSecundario);
            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(style => style.FontSize(8).FontColor(CorTextoSecundario));
                text.Span("Pagina ");
                text.CurrentPageNumber();
                text.Span(" de ");
                text.TotalPages();
            });
        });
    }

    private static string ObterTitulo(TipoRelatorio tipo)
    {
        return tipo switch
        {
            TipoRelatorio.RESUMO_FINANCEIRO => "Resumo financeiro mensal",
            TipoRelatorio.SERVICOS_MAIS_REALIZADOS => "Servicos mais realizados",
            TipoRelatorio.AGENDA_MENSAL => "Agenda mensal",
            _ => throw new ArgumentOutOfRangeException(nameof(tipo))
        };
    }

    private static string ObterDescricao(TipoRelatorio tipo)
    {
        return tipo switch
        {
            TipoRelatorio.RESUMO_FINANCEIRO =>
                "Valores calculados somente a partir de atendimentos concluidos.",
            TipoRelatorio.SERVICOS_MAIS_REALIZADOS =>
                "Ranking por quantidade de atendimentos concluidos no periodo.",
            TipoRelatorio.AGENDA_MENSAL =>
                "Visao consolidada de todos os agendamentos e seus status.",
            _ => throw new ArgumentOutOfRangeException(nameof(tipo))
        };
    }

    private static string FormatarMoeda(decimal valor)
    {
        return valor.ToString("C", CulturaBrasil);
    }

    private static string FormatarStatus(string status)
    {
        return status.Trim().ToUpperInvariant() switch
        {
            "AGENDADO" => "Agendado",
            "CONCLUIDO" => "Concluido",
            "CANCELADO" => "Cancelado",
            _ => status
        };
    }
}
