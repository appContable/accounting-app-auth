using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Swashbuckle.AspNetCore.Annotations;
using AccountCore.DTO.Parser;
using AccountCore.Services.Parser.Interfaces;
using AccountCore.DAL.Parser.Models;
using UglyToad.PdfPig;
using System.Text;

namespace AccountCore.API.Controllers
{
    /// <summary>
    /// Endpoints de testing para desarrollo - NO requieren autenticación
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [AllowAnonymous]
    public class TestController : ControllerBase
    {
        private readonly IPdfParsingService _parserService;
        private readonly ICategorizationService _categorizationService;

        public TestController(
            IPdfParsingService parserService,
            ICategorizationService categorizationService)
        {
            _parserService = parserService;
            _categorizationService = categorizationService;
        }

        /// <summary>
        /// Parsea PDF - si se especifica 'bank' usa el parser, sino devuelve texto raw
        /// </summary>
        [HttpPost("parse-pdf")]
        [Consumes("multipart/form-data")]
        [SwaggerOperation(Summary = "Parsea PDF con parser específico o devuelve texto raw (testing only)")]
        [SwaggerResponse(StatusCodes.Status200OK, "Resultado del parseo o texto raw del PDF")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Solicitud inválida")]
        public async Task<IActionResult> ParsePdf([FromForm] UploadPdfRequest req, CancellationToken ct)
        {
            if (req.File is null || req.File.Length == 0)
                return BadRequest("Archivo no válido.");

            try
            {
                using var stream = req.File.OpenReadStream();
                
                // Si se especifica bank, usar el parser correspondiente
                if (!string.IsNullOrWhiteSpace(req.Bank))
                {
                    var testUserId = "test-user-" + Guid.NewGuid().ToString("N")[..8];
                    
                    var result = await _parserService.ParseAsync(stream, req.Bank, testUserId, ct);
                    if (result == null) 
                        return BadRequest("No se pudo procesar el PDF con el parser especificado.");

                    // Aplicar categorización con reglas de banco
                    await _categorizationService.ApplyAsync(result, req.Bank, testUserId, ct);

                    return Ok(result);
                }
                else
                {
                    // Si no se especifica bank, devolver texto raw
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    ms.Position = 0;

                    ct.ThrowIfCancellationRequested();
                    
                    string rawText;
                    using (var doc = PdfDocument.Open(ms))
                    {
                        rawText = ExtractRawTextFromPdf(doc, ct);
                    }

                    return Ok(new
                    {
                        FileName = req.File.FileName,
                        FileSize = req.File.Length,
                        RawText = rawText,
                        CharacterCount = rawText.Length,
                        LineCount = rawText.Split('\n').Length,
                        ExtractedAt = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Error procesando PDF: {ex.Message}");
            }
        }

        /// <summary>
        /// Parsea un extracto PDF completo sin autenticación (solo para testing)
        /// </summary>
        [HttpPost("parse-pdf-full")]
        [Consumes("multipart/form-data")]
        [SwaggerOperation(Summary = "Parsea PDF completo sin autenticación (testing only)")]
        [SwaggerResponse(StatusCodes.Status200OK, "Resultado del parseo completo", typeof(ParseResult))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Solicitud inválida")]
        public async Task<IActionResult> ParsePdfFull([FromForm] UploadPdfRequest req, CancellationToken ct)
        {
            if (req.File is null || req.File.Length == 0)
                return BadRequest("Archivo no válido.");
            if (string.IsNullOrWhiteSpace(req.Bank))
                return BadRequest("Debe indicar el banco.");

            try
            {
                using var stream = req.File.OpenReadStream();
                
                // Usar un userId de testing
                var testUserId = "test-user-" + Guid.NewGuid().ToString("N")[..8];
                
                var result = await _parserService.ParseAsync(stream, req.Bank, testUserId, ct);
                if (result == null) 
                    return BadRequest("No se pudo procesar el PDF.");

                // Aplicar categorización con reglas de banco (sin reglas de usuario)
                await _categorizationService.ApplyAsync(result, req.Bank, testUserId, ct);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error procesando PDF: {ex.Message}");
            }
        }

        /// <summary>
        /// Extrae el texto raw del PDF sin ninguna transformación
        /// </summary>
        private static string ExtractRawTextFromPdf(PdfDocument doc, CancellationToken ct)
        {
            var sbDoc = new StringBuilder(capacity: 64_000);

            for (int p = 1; p <= doc.NumberOfPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(p);

                sbDoc.AppendLine($"=== PÁGINA {p} ===");
                
                // Extraer texto tal como viene del PDF, sin transformaciones
                var pageText = page.Text ?? string.Empty;
                sbDoc.AppendLine(pageText);
                sbDoc.AppendLine();
            }
            
            return sbDoc.ToString();
        }

        /// <summary>
        /// Prueba las reglas de categorización con texto de ejemplo
        /// </summary>
        [HttpPost("test-categorization")]
        [SwaggerOperation(Summary = "Prueba categorización con datos de ejemplo")]
        [SwaggerResponse(StatusCodes.Status200OK, "Resultado de categorización")]
        public async Task<IActionResult> TestCategorization([FromBody] TestCategorizationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Bank) || string.IsNullOrWhiteSpace(request.Description))
                return BadRequest("Bank y Description son requeridos.");

            var testResult = new ParseResult
            {
                Statement = new BankStatement
                {
                    Bank = request.Bank,
                    Accounts = new List<AccountStatement>
                    {
                        new()
                        {
                            AccountNumber = "TEST-ACCOUNT",
                            Transactions = new List<Transaction>
                            {
                                new()
                                {
                                    Date = DateTime.Today,
                                    Description = request.Description,
                                    Amount = request.Amount ?? -100m,
                                    Balance = 1000m,
                                    Type = (request.Amount ?? -100m) < 0 ? "debit" : "credit"
                                }
                            }
                        }
                    }
                }
            };

            var testUserId = "test-user-categorization";
            await _categorizationService.ApplyAsync(testResult, request.Bank, testUserId);

            return Ok(new
            {
                OriginalDescription = request.Description,
                Category = testResult.Statement.Accounts[0].Transactions[0].Category,
                Subcategory = testResult.Statement.Accounts[0].Transactions[0].Subcategory,
                CategorySource = testResult.Statement.Accounts[0].Transactions[0].CategorySource,
                AppliedAmount = testResult.Statement.Accounts[0].Transactions[0].Amount
            });
        }

        /// <summary>
        /// Endpoint de health check
        /// </summary>
        [HttpGet("health")]
        [SwaggerOperation(Summary = "Health check del servicio")]
        public IActionResult Health()
        {
            var version = HttpContext.RequestServices
                .GetRequiredService<IConfiguration>()["Api:Version"] ?? "1.0.0";
            
            return Ok(new
            {
                Status = "Healthy",
                Timestamp = DateTime.UtcNow,
                Version = version,
                BuildNumber = version.Split('.').LastOrDefault(),
                Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown"
            });
        }
    }

    public class TestCategorizationRequest
    {
        public string Bank { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public decimal? Amount { get; set; }
    }
}