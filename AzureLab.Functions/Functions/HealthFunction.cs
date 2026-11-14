using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace AzureLab_Functions.Functions
{
    public sealed class HealthFunction(ILogger<HealthFunction> logger)
    {
        private static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";

        [Function("health")]
        public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequest req)
        {
            logger.LogDebug("Health check triggered from {RemoteIpAddress}", req.HttpContext.Connection.RemoteIpAddress);
            var healthInfo = new
            {
                Status = "Healthy",
                Version,
                Timestamp = DateTime.UtcNow
            };
            return new OkObjectResult(healthInfo);
        }
    }
}
