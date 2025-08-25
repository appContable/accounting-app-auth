using System.Text;
using System.Text.RegularExpressions;
// 👇 Alias para modelos del DAL
using DAL = AccountCore.DAL.Parser.Models;

using AccountCore.DTO.Parser.Settings;
using AccountCore.Services.Parser.Exceptions;
using AccountCore.Services.Parser.Interfaces;
using AccountCore.Services.Parser.Parsers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Microsoft.Extensions.Options;

namespace AccountCore.Services.Parser
{
    public class PdfParserService : IPdfParsingService
    {
        private readonly IDictionary<string, IBankStatementParser> _parsers;
        private readonly IParseUsageRepository _usageRepository;
        private readonly int _monthlyLimit;

        private static readonly bool DIAG = false;

        public PdfParserService(IParseUsageRepository usageRepository, IOptions<UsageSettings> usageOptions)
        {
            _parsers = new Dictionary<string, IBankStatementParser>(StringComparer.OrdinalIgnoreCase)
            {
                ["galicia"]     = new GaliciaStatementParser(),
                ["supervielle"] = new SupervielleStatementParser(),
            };

            _usageRepository = usageRepository;
            _monthlyLimit = usageOptions.Value.MonthlyLimit;
        }

        // Firma original
        public async Task<DAL.ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId)
        {
            return await ParseAsync(pdfStream, bank, userId, CancellationToken.None);
        }

        // Sobrecarga con CancellationToken
        public async Task<DAL.ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var start = new DateTime(now.Year, now.Month, 1);
            var count = await _usageRepository.CountByUserAsync(userId, start, now);
            if (count >= _monthlyLimit)
                throw new UsageLimitExceededException(_monthlyLimit);

            using var ms = new MemoryStream();
            await pdfStream.CopyToAsync(ms, ct);
            ms.Position = 0;

            string fullText;
            ct.ThrowIfCancellationRequested();
            using (var doc = PdfDocument.Open(ms))
            {
                fullText = ExtractAllTextWithLayout(doc, ct);
            }

            if (DIAG)
            {
                var lines = fullText.Replace("\r\n", "\n").Split('\n');
                Console.WriteLine($"[pdf-diag] pages={(int?)null}, lines={lines.Length}");
                foreach (var s in lines.Take(30)) Console.WriteLine("[pdf] " + s);
            }

            if (!_parsers.TryGetValue(bank, out var parser))
                return null;

            ct.ThrowIfCancellationRequested();

            var result = parser.Parse(fullText);
            if (result != null)
            {
                var usage = new DAL.ParseUsage { UserId = userId, Bank = bank, ParsedAt = DateTime.UtcNow };
                await _usageRepository.CreateAsync(usage);
            }
            return result;
        }

        // ---------- Helpers ----------
        private static string ExtractAllTextWithLayout(PdfDocument doc, CancellationToken ct)
        {
            var sbDoc = new StringBuilder(capacity: 64_000);

            for (int p = 1; p <= doc.NumberOfPages; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = doc.GetPage(p);

                if (page.Letters == null || page.Letters.Count == 0)
                {
                    sbDoc.AppendLine(page.Text ?? string.Empty);
                    sbDoc.AppendLine();
                    continue;
                }

                var lines = RebuildLines(page.Letters);
                foreach (var line in lines)
                {
                    ct.ThrowIfCancellationRequested();
                    sbDoc.AppendLine(line);
                }
                sbDoc.AppendLine();
            }
            return sbDoc.ToString();
        }

        private static List<string> RebuildLines(IReadOnlyList<Letter> letters)
        {
            var glyphs = letters
                .Where(l => !string.IsNullOrEmpty(l.Value) && !char.IsWhiteSpace(l.Value[0]))
                .Select(l => new Glyph
                {
                    Ch = l.Value!,
                    X  = (double)l.StartBaseLine.X,
                    Y  = (double)l.StartBaseLine.Y,
                    W  = (double)l.GlyphRectangle.Width
                })
                .OrderByDescending(g => g.Y)
                .ThenBy(g => g.X)
                .ToList();

            const double yTol = 1.5;
            var rows = new List<List<Glyph>>();
            foreach (var g in glyphs)
            {
                if (rows.Count == 0) { rows.Add(new List<Glyph> { g }); continue; }

                var lastRow = rows[^1];
                if (Math.Abs(g.Y - lastRow[0].Y) > yTol) rows.Add(new List<Glyph> { g });
                else lastRow.Add(g);
            }

            var result = new List<string>(rows.Count);
            foreach (var row in rows)
            {
                row.Sort((a, b) => a.X.CompareTo(b.X));
                var sb = new StringBuilder(row.Count + 20);

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

        private struct Glyph
        {
            public string Ch { get; set; }
            public double X  { get; set; }
            public double Y  { get; set; }
            public double W  { get; set; }
        }
    }
}
