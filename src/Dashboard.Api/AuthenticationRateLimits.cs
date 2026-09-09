using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Dashboard.Api;

public static class AuthenticationRateLimits
{
    // Use the actual network peer. Arbitrary X-Forwarded-For headers must never
    // allow a client to manufacture fresh rate-limit partitions. Proxied clients
    // share the proxy peer bucket; configure per-user limits at the trusted edge.
    public static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        foreach (var policy in new[] { "dashboard-login", "device-enrollment" })
            options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(
                policy + ":" + ClientKey(context), _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    }
}
