using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.Annotations;
using System.Security.Claims;
using AccountCore.DTO.Parser;                 // UploadPdfRequest
using AccountCore.DTO.Parser.Settings;        // UsageSettings
using AccountCore.Services.Parser.Exceptions;
using AccountCore.Services.Parser.Interfaces; // IPdfParsingService, ICategorizationService, IParseUsageRepository
using AccountCore.DAL.Parser.Models;

namespace AccountCore.API.Controllers
{
    /// <summary>
    /// Endpoints de parseo y métricas de uso.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class ParserController : ControllerBase
    {
        private readonly IPdfParsingService _parserService;
        private readonly ICategorizationService _categorizationService;
        private readonly IParseUsageRepository _usageRepository;
        private readonly int _monthlyLimit;

        public ParserController(
            IPdfParsingService parserService,
            ICategorizationService categorizationService,
            IParseUsageRepository usageRepository,
            IOptions<UsageSettings> usageOptions)
        {
            _parserService = parserService;
            _categorizationService = categorizationService;
            _usageRepository = usageRepository;
            _monthlyLimit = usageOptions.Value.MonthlyLimit;
        }

        /// <summary>
        /// Parsea un extracto bancario PDF y aplica categorización (reglas del banco + reglas aprendidas del usuario).
        /// </summary>
        [HttpPost("parse")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [Consumes("multipart/form-data")]
        [SwaggerOperation(Summary = "Parsea un extracto PDF y categoriza (reglas banco + usuario)")]
        [SwaggerResponse(StatusCodes.Status200OK, "Resultado del parseo", typeof(ParseResult))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Solicitud inválida", typeof(string))]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token no válido o expirado")]
        [SwaggerResponse(StatusCodes.Status429TooManyRequests, "Límite de uso alcanzado", typeof(string))]
        public async Task<IActionResult> Parse([FromForm] UploadPdfRequest req, CancellationToken ct)
        {
            if (req.File is null || req.File.Length == 0)
                return BadRequest("Archivo no válido.");
            if (string.IsNullOrWhiteSpace(req.Bank))
                return BadRequest("Debe indicar el banco.");

            // Extract user ID from JWT token
            var userId = User.FindFirst(ClaimsPrincipalExtensions.UserId)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
                return BadRequest("Token no contiene información de usuario válida.");

            try
            {
                using var stream = req.File.OpenReadStream();

                // 1) Parsear PDF (devuelve ParseResult del DAL)
                var result = await _parserService.ParseAsync(stream, req.Bank, userId);
                if (result == null) return BadRequest("No se pudo procesar el PDF.");

                // 2) Aplicar categorización (banco + usuario)
                await _categorizationService.ApplyAsync(result, req.Bank, userId, ct);

                return Ok(result);
            }
            catch (UsageLimitExceededException)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, "Usage limit reached.");
            }
        }

        /// <summary>
        /// Devuelve la cantidad de parseos usados por el usuario en el mes y los restantes.
        /// </summary>
        [HttpGet("usage")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        [SwaggerOperation(Summary = "Uso actual del usuario")]
        [SwaggerResponse(StatusCodes.Status200OK, "Información de uso del usuario")]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token no válido o expirado")]
        public async Task<IActionResult> GetUsage()
        {
            // Extract user ID from JWT token
            var userId = User.FindFirst(ClaimsPrincipalExtensions.UserId)?.Value;
            if (string.IsNullOrWhiteSpace(userId))
                return BadRequest("Token no contiene información de usuario válida.");

            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1);

            var count = await _usageRepository.CountByUserAsync(userId, start, now);

            return Ok(new { count, remaining = _monthlyLimit - count });
        }
    }
}
