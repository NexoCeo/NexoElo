namespace SaaS.Api.Security;

public static class AuthCookie
{
    public const string Name = "nexoceo_session";

    public static CookieOptions CreateOptions(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = request.IsHttps ? SameSiteMode.None : SameSiteMode.Lax,
        Path = "/",
        IsEssential = true
    };
}
