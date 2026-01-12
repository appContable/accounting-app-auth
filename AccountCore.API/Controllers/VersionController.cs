using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Swashbuckle.AspNetCore.Annotations;
using System.Reflection;
using System.Text.Json;
using AccountCore.Services.Parser.Interfaces;

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
        private readonly IAppVersionRepository _versionRepository;
        private readonly IWebHostEnvironment _env;

        public VersionController(IConfiguration configuration, IAppVersionRepository versionRepository, IWebHostEnvironment env)
        {
            _configuration = configuration;
            _versionRepository = versionRepository;
            _env = env;
        }

        /// <summary>
        /// Gets detailed API version information
        /// </summary>
        [HttpGet]
        [SwaggerOperation(Summary = "Get API version information")]
        [SwaggerResponse(StatusCodes.Status200OK, "Version information")]
        public async Task<IActionResult> GetVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var assemblyVersion = assembly.GetName().Version?.ToString() ?? "Unknown";
            var configVersion = _configuration["Api:Version"] ?? "1.0.0";
            var buildDate = GetBuildDate(assembly);
            var buildNumber = configVersion.Split('.').LastOrDefault() ?? "0";

            var latestChange = await GetLatestChange();

            return Ok(new
            {
                version = configVersion,
                buildDate = buildDate.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                buildNumber = buildNumber,
                assemblyVersion = assemblyVersion,
                environment = _env.EnvironmentName,
                framework = Environment.Version.ToString(),
                machineName = Environment.MachineName,
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                changelog = latestChange
            });
        }

        /// <summary>
        /// Gets full version history and evolution
        /// </summary>
        [HttpGet("history")]
        [SwaggerOperation(Summary = "Get full version history and evolution")]
        [SwaggerResponse(StatusCodes.Status200OK, "Full version history")]
        public async Task<IActionResult> GetHistory()
        {
            var history = await _versionRepository.GetAllAsync();
            if (history != null && history.Any())
            {
                return Ok(history);
            }

            // Fallback to changelog.json if DB is empty
            var changelogPath = Path.Combine(_env.ContentRootPath, "changelog.json");
            if (System.IO.File.Exists(changelogPath))
            {
                var content = await System.IO.File.ReadAllTextAsync(changelogPath);
                return Ok(JsonSerializer.Deserialize<object>(content));
            }

            return Ok(new List<object>());
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

        private async Task<object?> GetLatestChange()
        {
            try
            {
                var changelogPath = Path.Combine(_env.ContentRootPath, "changelog.json");
                if (System.IO.File.Exists(changelogPath))
                {
                    var content = await System.IO.File.ReadAllTextAsync(changelogPath);
                    var logs = JsonSerializer.Deserialize<List<JsonElement>>(content);
                    return logs?.FirstOrDefault();
                }
            }
            catch { /* Ignore */ }
            return null;
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