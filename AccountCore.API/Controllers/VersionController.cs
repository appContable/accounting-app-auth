using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Swashbuckle.AspNetCore.Annotations;
using System.Reflection;

namespace AccountCore.API.Controllers
{
    /// <summary>
    /// Información de versión de la API
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
        /// Obtiene información detallada de la versión
        /// </summary>
        [HttpGet]
        [SwaggerOperation(Summary = "Información de versión de la API")]
        [SwaggerResponse(StatusCodes.Status200OK, "Información de versión")]
        public IActionResult GetVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyVersion = assembly.GetName().Version?.ToString() ?? "Unknown";
            var configVersion = _configuration["Api:Version"] ?? "1.0.0";
            var buildNumber = configVersion.Split('.').LastOrDefault() ?? "0";

            return Ok(new
            {
                ApiVersion = configVersion,
                AssemblyVersion = assemblyVersion,
                BuildNumber = buildNumber,
                BuildDate = GetBuildDate(assembly),
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown",
                Framework = Environment.Version.ToString(),
                MachineName = Environment.MachineName,
                Timestamp = DateTime.UtcNow
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