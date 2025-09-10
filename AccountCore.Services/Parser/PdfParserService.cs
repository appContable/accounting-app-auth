using System.Text;
using System.Text.RegularExpressions;
using AccountCore.DAL.Parser.Models;
using AccountCore.DTO.Parser.Settings;
using AccountCore.Services.Parser.Exceptions;
using AccountCore.Services.Parser.Interfaces;
using AccountCore.Services.Parser.Parsers;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AccountCore.Services.Parser
{
    public class PdfParserService : IPdfParsingService
    {
        private readonly IDictionary<string, IBankStatementParser> _parsers;
        private readonly IParseUsageRepository _usageRepository;
        private readonly int _monthlyLimit;

        private const string LineDelimiter = "@@@";
        private const string PageDelimiterTemplate = "<<PAGE:{n}>>>";

        public PdfParserService(IParseUsageRepository usageRepository, IOptions<UsageSettings> usageOptions)
        {
            _parsers = new Dictionary<string, IBankStatementParser>(StringComparer.OrdinalIgnoreCase)
            {
                ["galicia"] = new GaliciaStatementParser(),
                ["supervielle"] = new SupervielleStatementParser(),
            };
            _usageRepository = usageRepository;
            _monthlyLimit = usageOptions.Value.MonthlyLimit;
        }

        public Task<ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId)
            => ParseAsync(pdfStream, bank, userId, CancellationToken.None);

        public async Task<ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId, CancellationToken ct)
        {
            // límite mensual
            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var count = await _usageRepository.CountByUserAsync(userId, monthStart, now);
            if (count >= _monthlyLimit) throw new UsageLimitExceededException(_monthlyLimit);

            using var ms = new MemoryStream();
            await pdfStream.CopyToAsync(ms, ct);
            ms.Position = 0;

            string fullText;
            using (var doc = PdfDocument.Open(ms))
            {
                fullText = ExtractPagesForParser(doc, ct);
            }

            if (!_parsers.TryGetValue(bank, out var parser)) return null;

            var result = parser.Parse(fullText);
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

        // ============================
        // Reconstrucción con layout
        // ============================

        private static string ExtractPagesForParser(PdfDocument doc, CancellationToken ct)
        {
            var sb = new StringBuilder(capacity: 64_000);

            // En página 1: PASAR TODO (para meta: titular, CUIT, cuenta, CBU, período).
            // De página 2 en adelante: quedar SOLO con la tabla de movimientos (sin encabezado ni pie).
            for (int p = 1; p <= doc.NumberOfPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(p);

                sb.AppendLine(PageDelimiterTemplate.Replace("{n}", p.ToString()));

                var lines = (page.Letters == null || page.Letters.Count == 0)
                    ? RebuildFallback(page)
                    : RebuildLines(page.Letters);

                if (p == 1)
                {
                    // PÁGINA 1 COMPLETA pero sin banner ni doc-id
                    foreach (var l in lines)
                    {
                        var clean = l.TrimEnd();
                        if (RxPageBanner.IsMatch(clean)) continue; // quita "Resumen... Página x / y"
                        if (RxDocId.IsMatch(clean)) continue;      // quita "20250131049285496P"
                        sb.AppendLine(clean + LineDelimiter);
                    }
                }
                else
                {
                    // Páginas >= 2: solo movimientos
                    foreach (var l in FilterMovementLines(lines))
                        sb.AppendLine(l + LineDelimiter);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static IEnumerable<string> RebuildFallback(Page page)
        {
            var txt = (page.Text ?? string.Empty).Replace("\r\n", "\n");
            foreach (var l in txt.Split('\n'))
                yield return l.TrimEnd();
        }

        private struct Glyph
        {
            public string Ch { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double W { get; set; }
        }

        private static List<string> RebuildLines(IReadOnlyList<Letter> letters)
        {
            var glyphs = letters
                .Where(l => !string.IsNullOrEmpty(l.Value) && !char.IsWhiteSpace(l.Value[0]))
                .Select(l => new Glyph
                {
                    Ch = l.Value!,
                    X = (double)l.StartBaseLine.X,
                    Y = (double)l.StartBaseLine.Y,
                    W = (double)l.GlyphRectangle.Width
                })
                .OrderByDescending(g => g.Y)
                .ThenBy(g => g.X)
                .ToList();

            const double yTol = 1.5;
            var rows = new List<List<Glyph>>();
            foreach (var g in glyphs)
            {
                if (rows.Count == 0) { rows.Add(new List<Glyph> { g }); continue; }
                var last = rows[^1];
                if (Math.Abs(g.Y - last[0].Y) > yTol) rows.Add(new List<Glyph> { g });
                else last.Add(g);
            }

            var result = new List<string>(rows.Count);
            foreach (var row in rows)
            {
                row.Sort((a, b) => a.X.CompareTo(b.X));
                var sb = new StringBuilder(row.Count + 32);
                Glyph? prev = null;
                foreach (var g in row)
                {
                    if (prev.HasValue)
                    {
                        var pv = prev.Value;
                        var gap = g.X - (pv.X + pv.W);
                        if (gap > Math.Max(pv.W, g.W) * 0.5) sb.Append(' ');
                    }
                    sb.Append(g.Ch);
                    prev = g;
                }
                var line = Regex.Replace(sb.ToString(), @"\s{2,}", " ").TrimEnd();
                if (line.Length > 0) result.Add(line);
            }
            return result;
        }

        // ============================
        // Filtro de movimientos (pág. >= 2)
        // ============================

        private static readonly Regex RxHeaderCols = new(
            @"(?i)\bFecha\b.*\bDescripci[oó]n\b.*\bOrigen\b.*\bCr[ée]dito\b.*\bD[ée]bito\b.*\bSaldo\b");

        private static readonly Regex RxTotalLine = new(@"(?i)^\s*Total\b");

        private static readonly Regex RxPageBanner = new(@"(?i)^Resumen de Cuenta Corriente.*P[aá]gina\s+\d+\s*/\s*\d+", RegexOptions.Compiled);

        private static readonly Regex RxDocId = new(@"^\d{10,}P$", RegexOptions.Compiled);

        private static IEnumerable<string> FilterMovementLines(IEnumerable<string> lines)
        {
            bool capture = false;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) { if (capture) yield return line; continue; }

                if (RxHeaderCols.IsMatch(line)) { capture = true; continue; }      // no incluir encabezado
                if (capture && RxTotalLine.IsMatch(line)) { capture = false; continue; } // no incluir "Total ..."
                if (capture) yield return line;
            }
        }
    }
}
