using System.Collections.Concurrent;
using System.Net;

namespace AccountCore.API.Middleware
{
    public class RateLimitingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RateLimitingMiddleware> _logger;
        private readonly ConcurrentDictionary<string, (DateTime lastRequest, int count)> _requests = new();
        private readonly TimeSpan _timeWindow = TimeSpan.FromMinutes(1);
        private readonly int _maxRequests = 100;

        public RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // No aplicar límites a peticiones OPTIONS (pre-vuelo CORS)
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                await _next(context);
                return;
            }

            var clientId = GetClientIdentifier(context);
            var now = DateTime.UtcNow;

            _logger.LogDebug("RateLimiting: Request from {ClientId} to {Path}", clientId, context.Request.Path);

            // Clean old entries periodically
            if (now.Second == 0) // Clean every minute
            {
                CleanOldEntries(now);
            }

            if (_requests.TryGetValue(clientId, out var requestInfo))
            {
                if (now - requestInfo.lastRequest < _timeWindow)
                {
                    if (requestInfo.count >= _maxRequests)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                        await context.Response.WriteAsync("Rate limit exceeded. Please try again later.");
                        return;
                    }
                    // Mantener la hora del primer request para una ventana fija, no deslizarla
                    _requests[clientId] = (requestInfo.lastRequest, requestInfo.count + 1);
                }
                else
                {
                    _requests[clientId] = (now, 1);
                }
            }
            else
            {
                _requests[clientId] = (now, 1);
            }

            await _next(context);
        }

        private string GetClientIdentifier(HttpContext context)
        {
            // Try to get user ID from JWT claims first
            var userId = context.User?.FindFirst("UserId")?.Value;
            if (!string.IsNullOrEmpty(userId))
                return $"user:{userId}";

            // Fallback to IP address
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return $"ip:{ip}";
        }

        private void CleanOldEntries(DateTime now)
        {
            var keysToRemove = _requests
                .Where(kvp => now - kvp.Value.lastRequest > _timeWindow)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _requests.TryRemove(key, out _);
            }
        }
    }
}