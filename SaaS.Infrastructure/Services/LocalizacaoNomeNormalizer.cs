using System.Globalization;
using System.Text;

namespace SaaS.Infrastructure.Services
{
    public static class LocalizacaoNomeNormalizer
    {
        private static readonly string[] PrefixosAdministrativos =
        [
            "municipio de ",
            "municipality of ",
            "cidade de ",
            "city of "
        ];

        public static string Normalizar(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            var ultimoEspaco = false;

            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                    continue;

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                    ultimoEspaco = false;
                }
                else if (!ultimoEspaco && builder.Length > 0)
                {
                    builder.Append(' ');
                    ultimoEspaco = true;
                }
            }

            return builder.ToString().Trim();
        }

        public static bool Equivale(string nomeBanco, IEnumerable<string> candidatos)
        {
            var nomeNormalizado = RemoverPrefixoAdministrativo(Normalizar(nomeBanco));
            return candidatos
                .Select(Normalizar)
                .Where(item => item.Length > 0)
                .Select(RemoverPrefixoAdministrativo)
                .Any(item => item == nomeNormalizado);
        }

        private static string RemoverPrefixoAdministrativo(string value)
        {
            foreach (var prefixo in PrefixosAdministrativos)
            {
                if (value.StartsWith(prefixo, StringComparison.Ordinal))
                    return value[prefixo.Length..];
            }

            return value;
        }
    }
}
