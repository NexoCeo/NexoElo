namespace SaaS.Domain.Enums
{
    public enum TipoUsuario
    {
        CLIENTE,
        PROFISSIONAL,
        AUTONOMO,
        EMPRESA
    }

    public static class TipoUsuarioExtensions
    {
        public static string ToDatabaseValue(this TipoUsuario tipoUsuario)
        {
            if (!Enum.IsDefined(typeof(TipoUsuario), tipoUsuario))
            {
                throw new ArgumentException($"Tipo de usuario invalido: {tipoUsuario}");
            }

            return tipoUsuario.ToString();
        }

        public static TipoUsuario FromDatabaseValue(string? value)
        {
            var normalizedValue = value?.Trim();

            if (string.IsNullOrWhiteSpace(normalizedValue) || int.TryParse(normalizedValue, out _))
            {
                throw new ArgumentException($"Tipo de usuario invalido: {value}");
            }

            if (Enum.TryParse<TipoUsuario>(normalizedValue, true, out var tipoUsuario) &&
                Enum.IsDefined(typeof(TipoUsuario), tipoUsuario))
            {
                return tipoUsuario;
            }

            throw new ArgumentException($"Tipo de usuario invalido: {value}");
        }
    }
}
