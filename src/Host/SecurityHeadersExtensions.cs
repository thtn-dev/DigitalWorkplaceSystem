using Microsoft.Extensions.Options;
using Shell.Identity;

namespace Shell.Host;

public static class SecurityHeadersExtensions
{
    private const string DefaultCsp =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        var requireHttps = app.ApplicationServices.GetRequiredService<IOptions<ShellIdentityOptions>>().Value.RequireHttps;

        return app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            if (!context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers.ContentSecurityPolicy = DefaultCsp;
            }

            if (requireHttps && context.Request.IsHttps)
            {
                headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            }

            return next(context);
        });
    }
}
