// SPDX-License-Identifier: MIT
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AgentKick75.App.Web;

internal static class ControlPageSecurity
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; " +
        "script-src 'self'; " +
        "style-src 'self'; " +
        "connect-src 'self'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "base-uri 'none'; " +
        "form-action 'none'; " +
        "frame-ancestors 'none'";

    public static IApplicationBuilder UseControlPageSecurity(
        this IApplicationBuilder application,
        ControlPageOptions options)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(options);

        return application.Use(async (context, next) =>
        {
            AddSecurityHeaders(context.Response.Headers);

            if (!IsLoopbackConnection(context) || !HasExpectedHost(context))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden);
                return;
            }

            if (HttpMethods.IsOptions(context.Request.Method) || HasCorsPreflightHeaders(context.Request))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden);
                return;
            }

            bool isHookIngress = IsHookIngress(context.Request);
            bool isWrite = IsWriteMethod(context.Request.Method);
            if (!isHookIngress && !HasAllowedOrigin(context, requireOrigin: isWrite))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden);
                return;
            }

            if (isHookIngress)
            {
                if (!HasValidToken(
                        context.Request,
                        ControlPageOptions.HookTokenHeaderName,
                        options.HookToken))
                {
                    await RejectAsync(context, StatusCodes.Status403Forbidden);
                    return;
                }
            }
            else if (isWrite &&
                     (!HasSameOriginFetchMetadata(context.Request) ||
                      !HasValidToken(
                          context.Request,
                          ControlPageOptions.TokenHeaderName,
                          options.WriteToken)))
            {
                await RejectAsync(context, StatusCodes.Status403Forbidden);
                return;
            }

            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The browser disconnected. There is no useful response left to write.
            }
            catch (BadHttpRequestException exception) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                AddSecurityHeaders(context.Response.Headers);
                await RejectAsync(context, exception.StatusCode);
            }
            catch
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.Clear();
                    AddSecurityHeaders(context.Response.Headers);
                    await RejectAsync(context, StatusCodes.Status500InternalServerError);
                }
            }
        });
    }

    private static bool IsLoopbackConnection(HttpContext context)
    {
        IPAddress? remoteAddress = context.Connection.RemoteIpAddress;
        IPAddress? localAddress = context.Connection.LocalIpAddress;

        return remoteAddress is not null &&
               localAddress is not null &&
               IPAddress.IsLoopback(remoteAddress) &&
               IPAddress.IsLoopback(localAddress);
    }

    private static bool HasExpectedHost(HttpContext context)
    {
        HostString host = context.Request.Host;
        return string.Equals(host.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal) &&
               host.Port == context.Connection.LocalPort;
    }

    private static bool HasAllowedOrigin(HttpContext context, bool requireOrigin)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            return !requireOrigin;
        }

        if (originValues.Count != 1 ||
            !Uri.TryCreate(originValues.ToString(), UriKind.Absolute, out Uri? origin))
        {
            return false;
        }

        return string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
               string.Equals(origin.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal) &&
               origin.Port == context.Connection.LocalPort &&
               string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal) &&
               string.IsNullOrEmpty(origin.Query) &&
               string.IsNullOrEmpty(origin.Fragment);
    }

    private static bool HasSameOriginFetchMetadata(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Sec-Fetch-Site", out var values))
        {
            return false;
        }

        return values.Count == 1 &&
               string.Equals(values.ToString(), "same-origin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidToken(HttpRequest request, string headerName, string expectedToken)
    {
        if (!request.Headers.TryGetValue(headerName, out var tokenValues) ||
            tokenValues.Count != 1)
        {
            return false;
        }

        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(tokenValues.ToString()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }

    private static bool IsHookIngress(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        request.Path.Equals("/api/v1/hooks/codex", StringComparison.Ordinal);

    private static bool HasCorsPreflightHeaders(HttpRequest request)
    {
        return request.Headers.ContainsKey("Access-Control-Request-Method") ||
               request.Headers.ContainsKey("Access-Control-Request-Headers");
    }

    private static bool IsWriteMethod(string method)
    {
        return HttpMethods.IsPost(method) ||
               HttpMethods.IsPut(method) ||
               HttpMethods.IsPatch(method) ||
               HttpMethods.IsDelete(method);
    }

    private static void AddSecurityHeaders(IHeaderDictionary headers)
    {
        headers["Cache-Control"] = "no-store";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=()";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
    }

    private static async Task RejectAsync(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json; charset=utf-8";
        string title = statusCode switch
        {
            StatusCodes.Status403Forbidden => "Local control request rejected.",
            StatusCodes.Status413PayloadTooLarge => "Local control request body is too large.",
            _ => "The local control request failed.",
        };
        await context.Response.WriteAsync($"{{\"title\":\"{title}\",\"status\":{statusCode}}}");
    }
}
