using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Swashbuckle.AspNetCore.Annotations;
using System.Reflection;

namespace AccountCore.API.Controllers
{
    /// <summary>
    /// API Version information endpoint
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class VersionController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public VersionController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Gets detailed API version information
        /// </summary>
        [HttpGet]
        [SwaggerOperation(Summary = "Get API version information")]
        [SwaggerResponse(StatusCodes.Status200OK, "Version information")]
        public IActionResult GetVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyVersion = assembly.GetName().Version?.ToString() ?? "Unknown";
            var configVersion = _configuration["Api:Version"] ?? "1.0.0";
            var buildDate = GetBuildDate(assembly);
            var buildNumber = configVersion.Split('.').LastOrDefault() ?? "0";

            return Ok(new
            {
                version = configVersion,
                buildDate = buildDate.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                buildNumber = buildNumber,
                assemblyVersion = assemblyVersion,
                environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown",
                framework = Environment.Version.ToString(),
                machineName = Environment.MachineName,
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });
        }

        /// <summary>
        /// Gets simplified version information (minimal response)
        /// </summary>
        [HttpGet("simple")]
        [SwaggerOperation(Summary = "Get simple version information")]
        [SwaggerResponse(StatusCodes.Status200OK, "Simple version information")]
        public IActionResult GetSimpleVersion()
        {
            var configVersion = _configuration["Api:Version"] ?? "1.0.0";
            var assembly = Assembly.GetExecutingAssembly();
            var buildDate = GetBuildDate(assembly);

            return Ok(new
            {
                version = configVersion,
                buildDate = buildDate.ToString("yyyy-MM-ddTHH:mm:ssZ")
            });
        }
        private static DateTime GetBuildDate(Assembly assembly)
        {
            try
            {
                var location = assembly.Location;
                if (string.IsNullOrEmpty(location))
                    return DateTime.MinValue;

                return new FileInfo(location).CreationTimeUtc;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}