namespace SaaS.Domain.Enums;

public enum TipoRelatorio
{
    RESUMO_FINANCEIRO,
    SERVICOS_MAIS_REALIZADOS,
    AGENDA_MENSAL
}

public static class TipoRelatorioExtensions
{
    public static bool TryFromRoute(string? value, out TipoRelatorio tipoRelatorio)
    {
        var normalized = value?
            .Trim()
            .Replace('-', '_')
            .ToUpperInvariant();

        return Enum.TryParse(normalized, out tipoRelatorio) &&
               Enum.IsDefined(tipoRelatorio);
    }

    public static string ToFileName(this TipoRelatorio tipoRelatorio)
    {
        return tipoRelatorio switch
        {
            TipoRelatorio.RESUMO_FINANCEIRO => "resumo-financeiro",
            TipoRelatorio.SERVICOS_MAIS_REALIZADOS => "servicos-mais-realizados",
            TipoRelatorio.AGENDA_MENSAL => "agenda-mensal",
            _ => throw new ArgumentOutOfRangeException(nameof(tipoRelatorio))
        };
    }
}
