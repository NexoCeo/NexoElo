using System.Security.Cryptography;

namespace SaaS.Infrastructure.Security;

public static class LegacyPasswordHasher
{
    public static bool VerificarSenha(string senha, string hashArmazenada)
    {
        byte[] hashBytes;
        try
        {
            hashBytes = Convert.FromBase64String(hashArmazenada);
        }
        catch (FormatException)
        {
            return false;
        }

        if (hashBytes.Length != 48)
            return false;

        var salt = hashBytes[..16];
        var hashSalvo = hashBytes[16..];
        using var pbkdf2 = new Rfc2898DeriveBytes(
            senha,
            salt,
            10_000,
            HashAlgorithmName.SHA256);
        var hashCalculado = pbkdf2.GetBytes(32);

        return CryptographicOperations.FixedTimeEquals(hashCalculado, hashSalvo);
    }
}
