using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AccountCore.DAL.Parser.Models;           // ParseResult, BankStatement, etc.
using AccountCore.DTO.Parser.Settings;         // UsageSettings, ParserSettings
using AccountCore.Services.Parser.Exceptions;  // UsageLimitExceededException
using AccountCore.Services.Parser.Interfaces;  // IPdfParsingService, IBankStatementParser, IParseUsageRepository
using AccountCore.Services.Parser.Parsers;     // GaliciaStatementParser, SupervielleStatementParser

using Microsoft.Extensions.Options;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AccountCore.Services.Parser
{
    public class PdfParserService : IPdfParsingService
    {
        // Delimitadores consumidos por los parsers
        private const string LineDelimiter = "@@@";
        private const string PageDelimiterTemplate = "<<PAGE:{n}>>>";

        // Heurísticas de filtrado (para bancos tipo Galicia)
        private static readonly Regex RxHeaderCols = new Regex(
            @"(?i)\b(Fecha|Fec\.)\b.*\b(Descripción|Concepto)\b.*\b(Cr[eé]dito)\b.*\b(D[ée]bito)\b.*\b(Saldo)\b",
            RegexOptions.Compiled);

        private static readonly Regex RxTotalLine = new Regex(
            @"(?i)^\s*(Total|SALDO\s+PERIODO\s+ACTUAL|Saldo\s+del\s+per[ií]odo\s+anterior)\b",
            RegexOptions.Compiled);

        private static readonly Regex RxPageBanner = new Regex(
            @"(?i)^Resumen\s+de\s+Cuenta(\s+Corriente)?\s+.*P[aá]gina\s+\d+\s*/\s*\d+",
            RegexOptions.Compiled);

        private static readonly Regex RxDocId = new Regex(
            @"^\d{10,}P$",
            RegexOptions.Compiled);

        // Dependencias y configuración
        private readonly IDictionary<string, IBankStatementParser> _parsers;
        private readonly IParseUsageRepository _usageRepository;
        private readonly int _monthlyLimit;

        private readonly string? _dumpDir;
        //private readonly bool _enableDump;

        public PdfParserService(
            IParseUsageRepository usageRepository,
            IOptions<UsageSettings> usageOptions,
            IOptions<ParserSettings> parser)
        {
            _usageRepository = usageRepository;
            _monthlyLimit = usageOptions.Value.MonthlyLimit;

            // Preferir variable de entorno PARSER_DUMP_DIR si existe; si no, usar ParserSettings.DumpDir
            //var envDump = Environment.GetEnvironmentVariable("PARSER_DUMP_DIR");
            //_dumpDir = !string.IsNullOrWhiteSpace(envDump) ? envDump : parser.Value.DumpDir;
            //_enableDump = parser.Value.EnableDump || !string.IsNullOrWhiteSpace(_dumpDir);

            // Parsers registrados
            _parsers = new Dictionary<string, IBankStatementParser>(StringComparer.OrdinalIgnoreCase)
            {
                ["galicia"] = new GaliciaStatementParser(),
                ["supervielle"] = new SupervielleStatementParser(),
                ["santander"] = new SantanderStatementParser(),
                ["bbva"] = new BbvaStatementParser(),
            };
        }

        public Task<ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId)
            => ParseAsync(pdfStream, bank, userId, CancellationToken.None);

        public async Task<ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId, CancellationToken ct)
        {

            // Límite mensual
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var count = await _usageRepository.CountByUserAsync(userId, monthStart, now);
            if (count >= _monthlyLimit)
                throw new UsageLimitExceededException(_monthlyLimit);

            // Cargar PDF en memoria
            using var ms = new MemoryStream();
            await pdfStream.CopyToAsync(ms, ct);
            ms.Position = 0;

            // Extraer texto (con o sin filtrado según banco)
            var preserveAll =
                string.Equals(bank, "supervielle", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bank, "santander", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bank, "bbva", StringComparison.OrdinalIgnoreCase);

            string fullText;

            using (var doc = PdfDocument.Open(ms))
            {
                ct.ThrowIfCancellationRequested();
                fullText = ExtractPagesForParser(doc, ct, preserveAll);
            }

            // runId y dumps
            var runId = Guid.NewGuid().ToString("N");
            // if (_enableDump && !string.IsNullOrWhiteSpace(_dumpDir))
            // {
            //     SafeDump(_dumpDir, runId, "pre.txt", fullText);
            // }

            // Seleccionar parser
            if (!_parsers.TryGetValue(bank, out var parser))
                return null;

            // Ejecutar parser (sin callback de progreso en esta versión, para compatibilidad)
            var result = parser.Parse(fullText);
            // if (result != null)
            // {
            //     result.Warnings ??= new List<string>();
            //     result.Warnings.Insert(0, $"[run] id={runId} bank={bank} user={userId} at={DateTime.UtcNow:o} len={fullText?.Length ?? 0}");

            //     var head = fullText ?? string.Empty;
            //     var preview = head.Length <= 1200 ? head : head.Substring(0, 1200) + " …[truncated]";
            //     // Mantengo tu estilo de normalización para que Swagger se vea consistente
            //     result.Warnings.Insert(1, "[fulltext.head] " + preview.Replace("\r", " ").Replace("\n", " \\n "));

            //     if (_enableDump && !string.IsNullOrWhiteSpace(_dumpDir))
            //     {
            //         var warningsJoined = string.Join(Environment.NewLine, result.Warnings);
            //         SafeDump(_dumpDir, runId, "warnings.log", warningsJoined);
            //     }
            // }

            // Registrar uso
            if (result != null)
            {
                await _usageRepository.CreateAsync(new ParseUsage
                {
                    UserId = userId,
                    Bank = bank,
                    ParsedAt = DateTime.UtcNow
                });
            }

            return result;
        }

        // ============================================================
        //              Extracción de texto por páginas
        // ============================================================
        private static string ExtractPagesForParser(PdfDocument doc, CancellationToken ct, bool preserveAll)
        {
            var sb = new StringBuilder(capacity: 64_000);

            for (int p = 1; p <= doc.NumberOfPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(p);

                sb.AppendLine(PageDelimiterTemplate.Replace("{n}", p.ToString()));

                var lines = (page.Letters == null || page.Letters.Count == 0)
                    ? RebuildFallback(page)
                    : RebuildLines(page.Letters);

                IEnumerable<string> outLines;

                if (preserveAll)
                {
                    // Supervielle (multicuenta): no filtramos nada en ninguna página
                    outLines = lines;
                }
                else
                {
                    // Galicia (u otros): página 1 completa sin banner/docId. Páginas >=2 solo movimientos.
                    if (p == 1)
                    {
                        var firstPage = new List<string>();
                        foreach (var l in lines)
                        {
                            var clean = (l ?? string.Empty).TrimEnd();
                            if (clean.Length == 0) { firstPage.Add(clean); continue; }
                            if (RxPageBanner.IsMatch(clean)) continue;
                            if (RxDocId.IsMatch(clean)) continue;
                            firstPage.Add(clean);
                        }
                        outLines = firstPage;
                    }
                    else
                    {
                        outLines = FilterMovementLines(lines);
                    }
                }

                foreach (var l in outLines)
                {
                    sb.AppendLine(((l ?? string.Empty).TrimEnd()) + LineDelimiter);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Reconstrucción de líneas a partir de Letters agrupando por coordenada Y (top→bottom) y X (left→right).
        /// </summary>
        private static IEnumerable<string> RebuildLines(IReadOnlyList<Letter> letters)
        {
            if (letters == null || letters.Count == 0)
            {
                yield break;
            }

            var ordered = letters
                .Where(l => !string.IsNullOrEmpty(l.Value))
                .OrderByDescending(l => l.StartBaseLine.Y)
                .ThenBy(l => l.StartBaseLine.X)
                .ToList();

            var line = new StringBuilder();
            double? currentY = null;
            const double yTol = 1.5; // tolerancia para agrupar por renglón
            double lastX = double.MinValue;
            double lastW = 0;

            foreach (var ch in ordered)
            {
                double y = ch.StartBaseLine.Y;
                double x = ch.StartBaseLine.X;
                double w = ch.GlyphRectangle.Width;

                if (currentY == null || Math.Abs(y - currentY.Value) > yTol)
                {
                    if (line.Length > 0)
                    {
                        yield return line.ToString();
                        line.Clear();
                    }
                    currentY = y;
                    lastX = x;
                    lastW = w;
                }
                else
                {
                    if (x - (lastX + lastW) > Math.Max(lastW, w) * 0.5)
                        line.Append(' ');
                    lastX = x;
                    lastW = w;
                }

                line.Append(ch.Value);
            }

            if (line.Length > 0)
                yield return line.ToString();
        }

        /// <summary>
        /// Fallback cuando la página no tiene Letters (texto embebido no disponible).
        /// </summary>
        private static IEnumerable<string> RebuildFallback(Page page)
        {
            // PdfPig no provee texto legible en este caso; mantenemos estructura con línea vacía.
            yield return string.Empty;
        }

        /// <summary>
        /// Filtra solo la sección de movimientos (para bancos estilo Galicia). No usar para multicuenta.
        /// </summary>
        private static IEnumerable<string> FilterMovementLines(IEnumerable<string> lines)
        {
            bool capture = false;

            foreach (var raw in lines)
            {
                var line = (raw ?? string.Empty).TrimEnd();
                if (line.Length == 0)
                {
                    if (capture) yield return line;
                    continue;
                }

                if (RxHeaderCols.IsMatch(line))
                {
                    capture = true;     // detectar inicio de tabla (no devolver la cabecera)
                    continue;
                }

                if (capture && RxTotalLine.IsMatch(line))
                {
                    capture = false;    // cortar al hallar línea de totales/cierre
                    continue;
                }

                if (capture) yield return line;
            }
        }

        // ============================================================
        //                         Dump helpers
        // ============================================================
        private static void SafeDump(string? baseDir, string runId, string fileName, string content)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseDir)) return;

                var dir = Path.Combine(baseDir, runId);
                Directory.CreateDirectory(dir);

                var fullPath = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMdd_HHmmssfff}_{runId}_{fileName}");
                File.WriteAllText(fullPath, content ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                // No romper el parseo por problemas de dump
            }
        }
    }
}

