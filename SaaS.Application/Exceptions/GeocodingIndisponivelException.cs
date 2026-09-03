namespace SaaS.Application.Exceptions
{
    public class GeocodingIndisponivelException : Exception
    {
        public GeocodingIndisponivelException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
