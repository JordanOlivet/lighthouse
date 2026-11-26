using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Lighthouse.Middleware;

public static class RateLimitingConfiguration
{
    public const string AuthPolicy = "auth";
    public const string RefreshPolicy = "refresh";
    public const string GeneralApiPolicy = "api";
    public const string ForgotPasswordPolicy = "forgot-password";
    public const string ResetPasswordPolicy = "reset-password";
    public const string OidcPolicy = "oidc";

    public static void ConfigureRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // Auth endpoints: 20 attempts per 15 minutes per client IP
            options.AddPolicy(AuthPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetClientIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(15),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // OIDC login endpoints: 30 requests per minute per client IP. Separate from
            // AuthPolicy so SSO round-trips do not consume the password-login budget.
            options.AddPolicy(OidcPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetClientIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Refresh token endpoint: 20 attempts per 15 minutes per client IP
            options.AddPolicy(RefreshPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetClientIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 20,
                        Window = TimeSpan.FromMinutes(15),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // General API: 100 requests per minute per authenticated user (falls back to IP)
            options.AddPolicy(GeneralApiPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetUserOrIpKey(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 10
                    }));

            // Forgot password: 5 attempts per hour per client IP
            options.AddPolicy(ForgotPasswordPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetClientIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Reset password: 5 attempts per hour per client IP
            options.AddPolicy(ResetPasswordPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: GetClientIp(httpContext),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromHours(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    }));

            // Global rejection response
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json";

                string retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfterTime)
                    ? retryAfterTime.TotalSeconds.ToString("0")
                    : "60";

                context.HttpContext.Response.Headers.RetryAfter = retryAfter;

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "Too many requests. Please try again later.",
                    errorCode = "RATE_LIMIT_EXCEEDED",
                    retryAfterSeconds = int.Parse(retryAfter)
                }, cancellationToken: token);
            };
        });
    }

    /// <summary>
    /// Resolves the real client IP. Behind the bundled nginx reverse proxy the socket
    /// remote address is always the proxy (127.0.0.1), so the forwarded headers set by
    /// nginx (X-Forwarded-For / X-Real-IP) must be used to partition per real client.
    /// </summary>
    private static string GetClientIp(HttpContext httpContext)
    {
        string? forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
        {
            // X-Forwarded-For may be a comma-separated chain: the first entry is the origin client.
            string first = forwardedFor.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first))
                return first;
        }

        string? realIp = httpContext.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(realIp))
            return realIp.Trim();

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>
    /// Partition key for authenticated endpoints: the user id when present, otherwise the client IP.
    /// </summary>
    private static string GetUserOrIpKey(HttpContext httpContext)
    {
        string? userId = httpContext.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return !string.IsNullOrEmpty(userId) ? $"user:{userId}" : $"ip:{GetClientIp(httpContext)}";
    }
}
