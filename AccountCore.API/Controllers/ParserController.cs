using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Swashbuckle.AspNetCore.Annotations;
using System;
using System.Threading;
using System.Threading.Tasks;

using AccountCore.DTO.Parser;                 // UploadPdfRequest
using AccountCore.DTO.Parser.Settings;        // UsageSettings
using AccountCore.Services.Parser.Exceptions;
using AccountCore.Services.Parser.Interfaces; // IPdfParsingService, ICategorizationService, IParseUsageRepository
using DAL = AccountCore.DAL.Parser.Models;

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
        [Consumes("multipart/form-data")]
        [SwaggerOperation(Summary = "Parsea un extracto PDF y categoriza (reglas banco + usuario)")]
        [SwaggerResponse(StatusCodes.Status200OK, "Resultado del parseo", typeof(DAL.ParseResult))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Solicitud inválida", typeof(string))]
        [SwaggerResponse(StatusCodes.Status429TooManyRequests, "Límite de uso alcanzado", typeof(string))]
        public async Task<IActionResult> Parse([FromForm] UploadPdfRequest req, CancellationToken ct)
        {
            if (req.File is null || req.File.Length == 0)
                return BadRequest("Archivo no válido.");
            if (string.IsNullOrWhiteSpace(req.Bank))
                return BadRequest("Debe indicar el banco.");
            if (string.IsNullOrWhiteSpace(req.UserId))
                return BadRequest("Debe indicar el userId.");

            try
            {
                using var stream = req.File.OpenReadStream();

                // 1) Parsear PDF (devuelve DAL.ParseResult)
                var result = await _parserService.ParseAsync(stream, req.Bank, req.UserId);
                if (result == null) return BadRequest("No se pudo procesar el PDF.");

                // 2) Aplicar categorización (banco + usuario)
                await _categorizationService.ApplyAsync(result, req.Bank, req.UserId, ct);

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
        [SwaggerOperation(Summary = "Uso actual del usuario")]
        public async Task<IActionResult> GetUsage([FromQuery] string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return BadRequest("userId requerido.");

            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1);

            // Nota: si tu repo tiene una sobrecarga con CancellationToken, podés pasarlo.
            var count = await _usageRepository.CountByUserAsync(userId, start, now);

            return Ok(new { count, remaining = _monthlyLimit - count });
        }
    }
}
