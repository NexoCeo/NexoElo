namespace SaaS.Domain.Enums
{
    public enum DiaFuncionamento
    {
        DOMINGO,
        SEGUNDA,
        TERCA,
        QUARTA,
        QUINTA,
        SEXTA,
        SABADO
    }

    public static class DiaFuncionamentoExtensions
    {
        public static string ToDatabaseValue(this DiaFuncionamento dia)
        {
            if (!Enum.IsDefined(typeof(DiaFuncionamento), dia))
                throw new ArgumentException($"Dia de funcionamento invalido: {dia}");

            return dia.ToString();
        }

        public static DiaFuncionamento FromDatabaseValue(string? value)
        {
            var normalizedValue = value?.Trim();

            if (string.IsNullOrWhiteSpace(normalizedValue) || int.TryParse(normalizedValue, out _))
                throw new ArgumentException($"Dia de funcionamento invalido: {value}");

            if (Enum.TryParse<DiaFuncionamento>(normalizedValue, true, out var dia) &&
                Enum.IsDefined(typeof(DiaFuncionamento), dia))
            {
                return dia;
            }

            throw new ArgumentException($"Dia de funcionamento invalido: {value}");
        }

        public static DiaFuncionamento FromDayOfWeek(DayOfWeek dayOfWeek)
        {
            return dayOfWeek switch
            {
                DayOfWeek.Sunday => DiaFuncionamento.DOMINGO,
                DayOfWeek.Monday => DiaFuncionamento.SEGUNDA,
                DayOfWeek.Tuesday => DiaFuncionamento.TERCA,
                DayOfWeek.Wednesday => DiaFuncionamento.QUARTA,
                DayOfWeek.Thursday => DiaFuncionamento.QUINTA,
                DayOfWeek.Friday => DiaFuncionamento.SEXTA,
                DayOfWeek.Saturday => DiaFuncionamento.SABADO,
                _ => throw new ArgumentException($"Dia da semana invalido: {dayOfWeek}")
            };
        }
    }
}
