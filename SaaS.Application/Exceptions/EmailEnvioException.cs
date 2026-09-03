namespace SaaS.Application.Exceptions;

public sealed class EmailEnvioException : Exception
{
    public string Codigo { get; }

    public EmailEnvioException(string message, Exception innerException)
        : this("smtp_unknown", message, innerException)
    {
    }

    public EmailEnvioException(
        string codigo,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Codigo = codigo;
    }
}
