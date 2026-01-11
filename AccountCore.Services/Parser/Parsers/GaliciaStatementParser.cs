using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Parsers
{
    /// <summary>
    /// Transacción = bloque desde una línea que COMIENZA con fecha dd/MM/yy (ML) hasta ANTES de la próxima línea que comience con fecha.
    /// Reglas duras en el bloque:
    /// - EXACTAMENTE 2 montos válidos:
    ///     * Importe: formateo estricto (miles '.' y decimales ','), débito con '-' prefijo.
    ///     * Saldo:   formateo estricto, negativo con '-' sufijo.
    /// - Todo lo demás es descripción TAL CUAL.
    /// Meta:
    /// - PeriodStart/PeriodEnd desde fechas en portada (si hay 2).
    /// - Opening/Closing desde portada con ancla '$': closing = $…- ; opening = $… (positivo) en la sección “Saldos”
    ///   o en su defecto el mayor $ positivo de la portada.
    /// 
    /// Cambios clave:
    /// - NO se reconcilia automáticamente el importe.
    /// - Se marca Transaction.Suspicious y se propone Transaction.SuggestedAmount (= balance - prevBalance) cuando no cierra.
    /// - Heurísticas suaves de “consistencia semántica” (pistas de crédito/débito en la descripción).
    /// </summary>
    public class GaliciaStatementParser : IBankStatementParser
    {
        // ===== Patrones estrictos =====
        private static readonly Regex RxDateLineAnchor =
            new(@"(?m)^\s*(?<d>\d(?:\s?\d)\s*/\s*\d(?:\s?\d)\s*/\s*\d(?:\s?\d))\b", RegexOptions.Compiled);

        private const string MoneyCore = @"\d{1,3}(?:\.\d{3}){0,6},\d{2}";
        private static readonly Regex RxMoneyStrict = new($@"^(?:{MoneyCore})$", RegexOptions.Compiled);

        // Acepta: 1.234,56  | -1.234,56  | 1.234,56-
        private static readonly Regex RxStrictAmount = new(
            @"(?:(?<pref>-)\s*)?(?<m>\d{1,3}(?:\.\d{3})*,\s*\d\s*\d)(?<suff>\s*-)?",
            RegexOptions.Compiled);

        // ===== Regex para parsear montos y saldo (robusto con OCR) =====
        private static readonly Regex RxMoneyForParsing = new(
            @"\$?\s*(?<sign>(?:(?<=^)|(?<=\s))-)?(?<num>\d{1,3}(?:\.\d{3}){0,6},\d{2})(?<saldoNeg>-)?",
            RegexOptions.Compiled);

        private static decimal ParseEsDecimal(string num)
        {
            var clean = num.Replace(".", "").Replace(",", ".");
            return decimal.Parse(clean, CultureInfo.InvariantCulture);
        }

        private static string NormalizeOcrGlitches(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;
            t = Regex.Replace(t, @"-(\d)\s+(?=\1(?:\.\d{3}){1,6},\d{2}\b)", "-$1");
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d,\d{2}\b)", "");
            t = Regex.Replace(t, @"-\s+(?=\d)", "-");
            t = Regex.Replace(t, @"(?<=\d)\s+-\b", "-");
            return t;
        }

        // Devuelve SIEMPRE los DOS ÚLTIMOS montos del bloque (fallback).
        private static (decimal amount, decimal balance)? TryParseAmountAndBalance(string blockText)
        {
            var norm = NormalizeDashes(NormalizeOcrGlitches(blockText));
            var ms = RxMoneyForParsing.Matches(norm);
            if (ms.Count < 2) return null;

            var saldoM = ms[^1];
            var movM = ms[^2];

            decimal balance = ParseEsDecimal(saldoM.Groups["num"].Value);
            if (saldoM.Groups["saldoNeg"].Success) balance = -balance;

            decimal amount = ParseEsDecimal(movM.Groups["num"].Value);
            if (movM.Groups["sign"].Success) amount = -amount;

            return (amount, balance);
        }

        // Portada
        private static readonly Regex RxDollarAmount =
            new(@"\$\s*(?<m>\d{1,3}(?:\s?\.\s?\d{3})*,\s*\d\s*\d)(?<neg>-)?",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex RxAnyDate =
            new(@"\b(?<d>\d{2}\s*/\s*\d{2}\s*/\s*(?:\d{2}|\d{4}))\b", RegexOptions.Compiled);

        // Meta portada: N° de cuenta, CBU, CUIT
        private static readonly Regex RxAccountNumber =
            new(@"(?i)\bN[°º]\s*(?<raw>[\d\s\-]+)\b", RegexOptions.Compiled);
        private static readonly Regex RxCBU =
            new(@"(?i)\bCBU\b.*?(?<cbu>\d{22})", RegexOptions.Compiled);
        private static readonly Regex RxCUIT =
            new(@"(?i)\bCUIT\b.*?(?<cuit>\d{2}-\d{8}-\d)", RegexOptions.Compiled);

        private static readonly Regex RxMoneyDelimited = new(
            @"(?<![\p{L}\p{Nd}])\s*(-?\s*(?:\d{1,3}(?:\s?\.\s?\d{3})*),\s?\d{2}-?)\s*(?![\p{L}\p{Nd}])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        private static readonly Regex RxLeadingDate =
            new(@"(?m)^\s*\d{1,2}\s*/\s*\d{1,2}\s*/\s*\d{2}\s*", RegexOptions.Compiled);

        private static readonly Regex RxMoneyLooseDelimited = new(
            @"(?<![\p{L}\p{Nd}])\$?\s*-?\d{1,3}(?:\.\s*\d{3}){0,4},\s*\d\s*\d-?(?![\p{L}\p{Nd}])",
            RegexOptions.Compiled);

        private static readonly Regex RxMonthYearLine = new(
            @"(?mi)^\s*(enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|octubre|noviembre|diciembre)\s+\d{4}\s*$",
            RegexOptions.Compiled);

        private static readonly Regex RxHeaderStart = new(
            @"Datos\s+de\s+la\s+cuenta",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxHeaderEnd = new(
            @"\b(Movimientos|Fecha\s+Descripci[oó]n)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxDate = new(
            @"\b\d{2}/\d{2}/(?:\d{2}|\d{4})\b",
            RegexOptions.Compiled);

        private static readonly Regex RxMoneyHeader = new(
            @"\$?\s*-?\s*\d{1,3}(?:[ \.]\d{3})*,\s*\d\s*\d\s*-?",
            RegexOptions.Compiled);

        private static readonly Regex RxHeaderStopNotice = new(
            @"Dispon[eé]s de 30 d[ií]as.*?resumen\.|El cr[eé]dito fiscal discriminado.*?impositivos\.",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        private static decimal? ParseArs(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            var neg = false;
            if (s.EndsWith("-")) { neg = true; s = s[..^1]; }
            if (s.StartsWith("-")) { neg = true; s = s[1..]; }

            s = s.Replace("$", "").Replace(" ", "");
            s = s.Replace(".", "");
            s = s.Replace(",", ".");

            if (decimal.TryParse(s, NumberStyles.Number,
                                 CultureInfo.InvariantCulture, out var d))
                return neg ? -d : d;

            return null;
        }

        private static string SliceHeaderZone(string page1Text)
        {
            if (string.IsNullOrWhiteSpace(page1Text)) return page1Text;

            var start = RxHeaderStart.Match(page1Text);
            var end = RxHeaderEnd.Match(page1Text);

            int i0 = start.Success ? start.Index : 0;
            int i1 = end.Success && end.Index > i0 ? end.Index : page1Text.Length;

            var stop = RxHeaderStopNotice.Match(page1Text, i0);
            if (stop.Success && stop.Index > i0 && stop.Index < i1)
                i1 = stop.Index;

            return page1Text.Substring(i0, i1 - i0);
        }

        private static (decimal? opening, decimal? closing)
        ExtractHeaderBalancesByProximity(string page1Text, int proximityWindow = 80)
        {
            var header = SliceHeaderZone(page1Text);
            if (string.IsNullOrWhiteSpace(header)) header = page1Text;
            var hdr = TightenForNumbers(header);

            var dates = RxDate.Matches(header).Cast<Match>().ToList();
            var moneys = RxMoneyHeader.Matches(header).Cast<Match>().ToList();

            if (dates.Count < 2 || moneys.Count == 0) return (null, null);

            DateTime d1, d2;
            TryParseDate2Or4(dates.First().Value, out d1);
            TryParseDate2Or4(dates.Last().Value, out d2);
            var startDate = d1 <= d2 ? dates.First() : dates.Last();
            var endDate = d1 <= d2 ? dates.Last() : dates.First();

            Match? ClosestMoneyTo(Match date) =>
                moneys.Select(m => (m, dist: Math.Abs(m.Index - date.Index)))
                      .Where(x => x.dist <= proximityWindow)
                      .OrderBy(x => x.dist).Select(x => x.m).FirstOrDefault()
                ?? moneys.OrderBy(m => Math.Abs(m.Index - date.Index)).FirstOrDefault();

            var openM = ClosestMoneyTo(startDate);
            var closeM = ClosestMoneyTo(endDate);

            if (openM is not null && closeM is not null && openM.Index == closeM.Index) openM = null;

            var opening = ParseArs(openM?.Value ?? "");
            var closing = ParseArs(closeM?.Value ?? "");

            if (opening is null || opening <= 0) opening = null;
            if (closing is null || closing >= 0) closing = null;

            return (opening, closing);
        }

        private static (decimal? opening, decimal? closing) ExtractOpeningClosingDollar(string page1)
        {
            if (string.IsNullOrWhiteSpace(page1)) return (null, null);

            var headerRaw = SliceHeaderZone(page1);
            var header = TightenForNumbers(headerRaw);

            decimal? opening = null;
            decimal? closing = null;

            var dollarMatches = RxDollarAmount.Matches(header);

            var dates = RxAnyDate.Matches(header);
            Match? endDate = dates.Count > 0
                ? dates.Cast<Match>().OrderBy(m =>
                {
                    TryParseDate2Or4(m.Value, out var d); return -d.Ticks;
                }).First()
                : null;

            var dollars = RxDollarAmount.Matches(header).Cast<Match>().ToList();

            var negs = dollars.Where(m => m.Groups["neg"].Success).ToList();
            if (negs.Count > 0)
            {
                var pick = endDate is null
                    ? negs[0]
                    : negs.OrderBy(m => Math.Abs(m.Index - endDate.Index)).First();
                try { closing = ParseSaldo(pick.Groups["m"].Value + "-"); } catch { }
            }

            var idx = header.IndexOf("Saldos", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var slice = header.Substring(idx, Math.Min(300, header.Length - idx));
                foreach (Match m in RxDollarAmount.Matches(slice))
                    if (!m.Groups["neg"].Success && TryParseDecimalEs(m.Groups["m"].Value, out var v)) { opening = v; break; }
            }

            if (!opening.HasValue)
            {
                decimal? maxPos = null;
                foreach (var m in dollars)
                    if (!m.Groups["neg"].Success && TryParseDecimalEs(m.Groups["m"].Value, out var v))
                        if (maxPos is null || v > maxPos) maxPos = v;
                opening = maxPos;
            }

            return (opening, closing);
        }

        private static (decimal? opening, decimal? closing) ExtractHeaderBalances(string page1Text)
        {
            var (opProx, clProx) = ExtractHeaderBalancesByProximity(page1Text, proximityWindow: 80);
            var (opDollar, clDollar) = ExtractOpeningClosingDollar(page1Text);

            var opening = opProx ?? opDollar;
            var closing = clDollar ?? clProx;

            if (opening.HasValue && closing.HasValue && Math.Abs(opening.Value - closing.Value) < 0.005m)
                opening = null;
            if (opening.HasValue && opening.Value <= 0) opening = null;

            return (opening, closing);
        }

        private static readonly Regex RxAnyAmount =
            new Regex(@"-?\d{1,3}(?:\.\d{3})*,\d{2}-?", RegexOptions.Compiled);
        private static readonly Regex RxMoneyLoose = new(@"\-?\d{1,3}(?:[.\s]\d{3})*,\d{2}\-?", RegexOptions.Compiled);

        private static readonly Regex RxAllDigitsish = new(@"^[\d\s.\-]+$", RegexOptions.Compiled);
        private static readonly Regex RxCuit = new(@"\b\d{2}-\d{7,8}-\d\b", RegexOptions.Compiled);
        private static readonly Regex RxLongId = new(@"\b\d{10,}\b", RegexOptions.Compiled);
        private static readonly string[] DropTokens = { "PROPIA", "VARIOS", "CUENTA ORIGEN" };

        private static readonly Regex RxLongDigits = new(@"\b\d{10,}\b", RegexOptions.Compiled);
        private static readonly Regex RxMostlyDigits = new(@"^\s*[\d\s\.\-]+$", RegexOptions.Compiled);
        private static readonly Regex RxCodey = new(@"^[A-Z]{2,}\d{3,}$", RegexOptions.Compiled);
        private static readonly Regex RxStrayMinusDigit = new(@"(?<=\s)-\d{1,3}(?=\s|$)", RegexOptions.Compiled);

        private static readonly string[] OriginKeepHints = new[] {
            "HABERES","ACRED.HABERES",
            "PROVEEDORES",
            "FIMA PREMIUM",
            "REG.RECAU.SIRCREB",
            "AFIP","PLANRG",
            "D.A. AL VTO","BUSINESS",
            "BANCO","SANTANDER","NUEVO BANCO",
            "MERCANTIL","HONORARIOS"
        };

        private static bool ShouldKeepOriginLine(string s)
        {
            var t = s.Trim();
            if (t.Length == 0) return false;
            if (RxAnyAmount.IsMatch(t)) return false;
            if (RxCuit.IsMatch(t)) return false;
            if (RxLongDigits.IsMatch(t)) return false;
            if (RxMostlyDigits.IsMatch(t)) return false;
            if (RxCodey.IsMatch(t)) return false;
            if (t.Contains("0000000000")) return false;
            return OriginKeepHints.Any(h => t.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                   || t.Split(' ').Count(w => w.Length > 1) >= 1;
        }

        private static string PostClean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;
            s = s.Replace("@@@", " ");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            s = Regex.Replace(s, @"\s*—\s*—\s*", " — ");
            s = Regex.Replace(s, @"\s*·\s*·\s*", " · ");
            s = Regex.Replace(s, @"(?<=\s|^)-1(?=\s|$)", "");
            s = s.Trim(' ', '—', '·', '-');
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s;
        }

        private static readonly string[] DropStarts = {
            "Página", "<<PAGE:", "Resumen de ", "CBU", "N° ", "Credito", "Crédito", "Débito", "Saldo"
        };

        private static readonly string[] DropExact = {
            "PROPIA", "VARIOS", "CUENTA ORIGEN", "PROVEEDORES",
            "ACRED.HABERES", "REG.RECAU.SIRCREB"
        };

        private static bool LooksLikeUsefulName(string line)
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool twoWordsWithLetters = words.Count(w => w.Any(char.IsLetter)) >= 2;
            bool fundHints = line.Contains("FIMA", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("CLASE", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("PREMIUM", StringComparison.OrdinalIgnoreCase);
            return twoWordsWithLetters || fundHints;
        }

        private static List<string> ExtractExtraDetailLines(string blockLarge, int blockStart, int blockEnd, int maxLines)
        {
            var moneys = RxMoneyLoose.Matches(blockLarge);
            if (moneys.Count < 2) return new();

            int from = moneys[1].Index + moneys[1].Length;
            if (from < 0 || from >= blockLarge.Length) return new();

            int len = Math.Max(0, Math.Min(blockEnd - blockStart, blockLarge.Length - from));
            var tail = blockLarge.Substring(from, len);

            var keep = new List<string>(maxLines);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in tail.Split('\n'))
            {
                var l0 = raw.Trim();
                if (l0.Length == 0) continue;

                var l = PostClean(l0);
                if (l.Length == 0) continue;

                if (DropStarts.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                if (DropExact.Any(t => string.Equals(l, t, StringComparison.OrdinalIgnoreCase))) continue;
                if (RxAllDigitsish.IsMatch(l)) continue;
                if (RxCuit.IsMatch(l)) continue;
                if (RxLongId.IsMatch(l)) continue;

                if (!l.Any(char.IsLetter)) continue;
                if (!LooksLikeUsefulName(l)) continue;

                var norm = Regex.Replace(l, @"\s+", " ").Trim();
                if (seen.Contains(norm)) continue;
                seen.Add(norm);

                keep.Add(norm);
                if (keep.Count >= maxLines) break;
            }

            return keep;
        }

        private static string NormalizeDashes(string s) =>
            string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]", "-");

        private static string StripDelimEnd(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.EndsWith("@@@") ? s[..^3] : s);

        private static string TightenForNumbers(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            var t = Regex.Replace(s, @"\p{Zs}+", " ");
            t = t.Replace('\u00A0', ' ');
            t = NormalizeDashes(t);

            // Evitar que números de 3 dígitos sin miles (e.g. 411,21) pierdan dígitos por espacios raros del OCR
            t = Regex.Replace(t, @"(?<=^|\s)(\d)\s*(\d)\s*(\d),\s*(\d)\s*(\d)(?=\b)", "${1}${2}${3},${4}${5}");

            t = Regex.Replace(t, @"(?:@{3}|\r?\n)", " ");
            t = Regex.Replace(t,
                @"(?<=[\p{L}\p{Nd}])-(?=\s*\d{1,3}(?:\s?\.\s?\d{3})*,\s?\d{2}(?:-|\b))",
                " -");

            t = Regex.Replace(t, @"(?:@{3}|\r?\n)", " ");
            t = Regex.Replace(t, @"\s*\.\s*", ".");
            t = Regex.Replace(t, @"\s*,\s*", ",");
            t = Regex.Replace(t, @"(?<=\d)\s+(?=,\d{2}\b)", "");
            t = Regex.Replace(t, @",(?<a>\d)\s+(?<b>\d)\b", @",${a}${b}");
            t = Regex.Replace(
                t,
                @"(?<=^|\s)-\s*(?<head>\d{1,3})\s+(?<tail>\d{1,2})\s*,\s*(?<c1>\d)\s*(?<c2>\d)\b",
                "-${head}${tail},${c1}${c2}"
            );
            t = Regex.Replace(
                t,
                @"(?<=^|\s)-\s*(?<head>\d{1,3})\s+(?<tail>\d{1,2}),(?<c1>\d)(?<c2>\d)\b",
                "-${head}${tail},${c1}${c2}"
            );
            t = Regex.Replace(t, @"-(\d)\s+(?=\1(?:\.\d{3}){1,3},\d{2}\b)", "-$1");
            t = Regex.Replace(t, @"(?<=-1)\s+(?=\d{2}\.\d{3},\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d{1,2},\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\.\d{2,3})\s+(?=\d\.\d{3}(?:\.\d{3})*,\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d(?:\.\d{3}){1,6},\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\.)((?:\d\s+){1,2}\d)(?=,\d{2}\b)", m => m.Value.Replace(" ", ""));
            t = Regex.Replace(t, @"(?<=\.\d)\s+(?=\d(?:\s+\d)?,\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d{1,3},\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d{1,3}(?:,|\.\d{3},)\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=-1)\s+(?=\d{1,3}(?:\.\d{3})*,\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\b\d)\s+(?=\d{2}\.\d{3},\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\b\d)\s*(?:@@@|\r?\n)?\s+(?=\d{2}\.\d{3},\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=\b\d{1,3})\s+(?=\d{3},\d{2}\b)", ".");
            t = Regex.Replace(t, @"(?<=\b\d{1,3})\s*(?:@@@|\r?\n)?\s+(?=\d{3},\d{2}\b)", ".");
            t = Regex.Replace(t, @"(?<=^|\s)-\s*(?<ent>\d{1,3})\s*,\s*(?<a>\d)\s*(?<b>\d)\b", "-${ent},${a}${b}");
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d\.\d{3}(?:\.\d{3})*,\d{2}\b)", "");
            t = Regex.Replace(t, @"(?<=^|\s)(?<lead>\d{1,2})\s+(?=\d{1,3}(?:\.\d{3}){1,3},\d{2}(?:-|\b))", "${lead}");
            t = Regex.Replace(t, @"-\s+(?=\d)", "-");
            t = Regex.Replace(t, @"(?<=\d)\s+-\b", "-");
            t = Regex.Replace(t, @"[ \t]{2,}", " ");
            t = Regex.Replace(t, @",(?<a>\d)\s+(?<b>\d)\b", @",${a}${b}");
            t = Regex.Replace(t, @"(?<=\.\d{1,2})\s+(?=\d,\d{2}\b)", "");
            return t;
        }

        private static string FixInlineMinusBeforeMoney(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = NormalizeDashes(s);
            return Regex.Replace(
                s,
                @"(?<=[\p{L}\p{Nd}])-(?=\s*\d{1,3}(?:\s?\.\s?\d{3})*,\s?\d{2}(?:-|\b))",
                " -"
            );
        }

        private static string NormalizeMoneyToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            s = s.Replace('\u00A0', ' ');
            s = Regex.Replace(s, @"\s+", "");
            return s;
        }

        private static readonly Regex RxStrictAmountCore = new(
            @"^\d{1,3}(?:\.\d{3})*,\d{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        private static (string? accountDigits, string? cbu, string? cuit) ExtractAccountMetaFromPage1(string page1)
        {
            string? acc = null, cbu = null, cuit = null;

            var mAcc = RxAccountNumber.Match(page1);
            if (mAcc.Success)
            {
                var raw = mAcc.Groups["raw"].Value;
                var rawAcc = new string(raw.Where(char.IsDigit).ToArray());
                acc = $"{rawAcc.Substring(0, 7)}-{rawAcc[7]} {rawAcc.Substring(8, 3)}-{rawAcc[11]}";
                if (string.IsNullOrWhiteSpace(acc)) acc = null;
            }

            var mCBU = RxCBU.Match(page1);
            if (mCBU.Success) cbu = mCBU.Groups["cbu"].Value;

            var mCUIT = RxCUIT.Match(page1);
            if (mCUIT.Success) cuit = mCUIT.Groups["cuit"].Value;

            return (acc, cbu, cuit);
        }

        private static bool TryParseDecimalEs(string s, out decimal value)
        {
            var norm = (s ?? string.Empty).Replace(".", "").Replace(",", ".");
            return decimal.TryParse(norm, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseDateDdMmYy(string s, out DateTime d)
        {
            var clean = Regex.Replace(s ?? "", @"\s+", "");
            clean = Regex.Replace(clean, @"\s*/\s*", "/");
            return DateTime.TryParseExact(clean, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }

        private static bool TryParseDate2Or4(string s, out DateTime d)
        {
            var norm = Regex.Replace(s, @"\s*/\s*", "/");
            string[] fmts = { "dd/MM/yy", "dd/MM/yyyy" };
            return DateTime.TryParseExact(norm, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }

        private static bool IsStrictMoney(string tokenCore /* sin signo */)
        {
            var t = NormalizeMoneyToken(tokenCore);
            return RxStrictAmountCore.IsMatch(t);
        }

        private static decimal ParseImporte(string token)
        {
            var s = NormalizeDashes(token).Trim();
            bool neg = s.StartsWith("-");
            s = s.TrimStart('-');
            if (!IsStrictMoney(s)) throw new FormatException($"Monto (importe) fuera de formato estricto: '{token}'");
            var val = decimal.Parse(s.Replace(".", "").Replace(",", "."), CultureInfo.InvariantCulture);
            return neg ? -val : val;
        }

        private static decimal ParseSaldo(string token)
        {
            var s = NormalizeDashes(token).Trim();
            bool neg = s.EndsWith("-");
            s = s.TrimEnd('-');
            if (!IsStrictMoney(s)) throw new FormatException($"Monto (saldo) fuera de formato estricto: '{token}'");
            var val = decimal.Parse(s.Replace(".", "").Replace(",", "."), CultureInfo.InvariantCulture);
            return neg ? -val : val;
        }

        private static string ExtractPageText(string full, int page)
        {
            var tag = $"<<PAGE:{page}>>>";
            var idx = full.IndexOf(tag, StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            var from = idx + tag.Length;
            var next = full.IndexOf("<<PAGE:", from, StringComparison.Ordinal);
            return next >= 0 ? full[from..next] : full[from..];
        }

        private static (DateTime? start, DateTime? end) ExtractPeriodFromPage1(string page1)
        {
            var ms = RxAnyDate.Matches(page1);
            if (ms.Count < 2) return (null, null);

            var parsed = new List<DateTime>();
            foreach (Match m in ms)
            {
                if (TryParseDate2Or4(m.Groups["d"].Value, out var d))
                    parsed.Add(d);
            }
            if (parsed.Count < 2) return (null, null);
            parsed.Sort();
            return (parsed[0], parsed[^1]);
        }

        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s.Substring(0, n) + " …[truncated]");

        private static string TrimForWarn(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var cleaned = s.Replace("\n", " ").Replace("\r", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            return Trunc(cleaned, 180);
        }

        private const decimal EPS = 0.05m;
        private static bool NearlyEq(decimal a, decimal b, decimal eps = EPS)
            => Math.Abs(a - b) <= eps;

        // === NUEVO: heurística de consistencia semántica crédito/débito a partir de la descripción ===
        private static (bool? expectsCredit, string? reason) GuessSignFromDescription(string desc)
        {
            if (string.IsNullOrWhiteSpace(desc)) return (null, null);
            var d = desc.ToUpperInvariant();

            // pistas típicas
            if (d.Contains("CRED ") || d.Contains("ACRED") || d.Contains("ACREDIT") || d.Contains("CRED BCA") || d.Contains("CREDITO"))
                return (true, "desc_sugiere_credito");
            if (d.Contains("DEB ") || d.Contains("DÉB ") || d.Contains("DEBIT") || d.Contains("DÉBIT") || d.Contains("DÉBITO") || d.Contains("DEBITO"))
                return (false, "desc_sugiere_debito");

            return (null, null);
        }

        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress = null)
        {
            var result = new ParseResult
            {
                Statement = new BankStatement { Bank = "Banco Galicia", Accounts = new List<AccountStatement>() },
                Warnings = new List<string>()
            };

            var full = (text ?? string.Empty).Replace("\r\n", "\n");
            int blocksParsedCount = 0;
            int blocksSkippedNo2Count = 0;
            int fallbackUsedCount = 0;
            int pageCutRetriedCount = 0;
            int dashTrimmedBeforeMoneyCount = 0;

            // 1. Normalizar formato dd / mm / yy -> dd/mm/yy
            full = Regex.Replace(
                full,
                @"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2})\s*/\s*(\d{2})(?!\d)",
                "$1/$2/$3",
                RegexOptions.Compiled);

            // 2. Si la fecha no está al inicio de la línea, movemos el texto previo (montos/desc)
            // a después de la fecha para que queden en el bloque que inicia con esa fecha.
            // Esto resuelve casos de wrap-around donde los montos quedan al final de la línea física anterior.
            full = string.Join("\n", full.Split('\n').Select(line =>
            {
                var m = Regex.Match(line, @"^(?<pre>.+?)\b(?<date>\d{1,2}/\d{1,2}/\d{2})\b");
                if (m.Success)
                {
                    var pre = m.Groups["pre"].Value;
                    if (!string.IsNullOrWhiteSpace(pre) && !pre.Contains("<<PAGE:"))
                    {
                        return m.Groups["date"].Value + " " + pre + line.Substring(m.Length);
                    }
                }
                return line;
            }));

            // 3. Forzar salto de línea para cualquier fecha que haya quedado pegada
            full = Regex.Replace(
                full,
                @"(?<![\r\n])(?<!^)(?<!\d)(\d{1,2}/\d{1,2}/\d{2})",
                "\n$1",
                RegexOptions.Compiled);

            full = Regex.Replace(
                full,
                @"(?<!\d)(\d)\s+(?=\1/\d{2}/\d{2})",
                "$1",
                RegexOptions.Compiled);

            // Portada
            var page1 = ExtractPageText(full, 1);
            var (openingP1, closingP1) = ExtractHeaderBalances(page1);

            var (pStart, pEnd) = ExtractPeriodFromPage1(page1);

            var (accDigits, cbu, cuit) = ExtractAccountMetaFromPage1(page1);
            if (!string.IsNullOrEmpty(accDigits)) result.Warnings.Add($"[meta.accountNumberDigits] {accDigits}");
            if (!string.IsNullOrEmpty(cbu)) result.Warnings.Add($"[meta.cbu] {cbu}");
            if (!string.IsNullOrEmpty(cuit)) result.Warnings.Add($"[meta.cuit] {cuit}");

            var account = new AccountStatement();
            if (!string.IsNullOrEmpty(accDigits)) account.AccountNumber = accDigits;
            result.Statement.Accounts.Add(account);

            // Anclas de fecha
            var matches = RxDateLineAnchor.Matches(full);
            if (matches.Count == 0)
            {
                result.Warnings.Add("No se detectaron líneas con fecha al inicio.");
                if (pStart.HasValue && pEnd.HasValue)
                {
                    result.Statement.PeriodStart = pStart.Value;
                    result.Statement.PeriodEnd = pEnd.Value;
                }
                account.OpeningBalance = openingP1;
                account.ClosingBalance = closingP1;
                return result;
            }

            decimal? prevRunningBalance = openingP1; // *** NO se fuerza importe, solo se usa para expected ***

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int endNextDate = (i + 1 < matches.Count) ? matches[i + 1].Index : full.Length;
                int endCandidate = endNextDate;

                int nextPageIdx = full.IndexOf("<<PAGE:", start, StringComparison.Ordinal);
                bool cutByPage = nextPageIdx >= 0 && nextPageIdx < endCandidate;

                string blockLarge = full.Substring(start, (cutByPage ? nextPageIdx : endCandidate) - start);

                var normalized = TightenForNumbers(blockLarge);
                var amts = RxStrictAmount.Matches(normalized);

                if (amts.Count < 2 && cutByPage) { pageCutRetriedCount++; }
                if (amts.Count < 2 && cutByPage)
                {
                    blockLarge = full.Substring(start, endCandidate - start);
                    normalized = TightenForNumbers(blockLarge);
                    amts = RxStrictAmount.Matches(normalized);
                }

                if (amts.Count < 2)
                    continue;

                int cutEndNorm = amts[1].Index + amts[1].Length;
                var blockNormCut = normalized.Substring(0, cutEndNorm);
                var blockForDesc = blockNormCut.Replace("@@@", "\n");

                var blockDesc = string.Join("\n",
                    blockForDesc
                        .Split('\n')
                        .Select(StripDelimEnd)
                        .Where(line =>
                            !line.TrimStart().StartsWith("<<PAGE:", StringComparison.OrdinalIgnoreCase) &&
                            !line.TrimStart().StartsWith("Página", StringComparison.OrdinalIgnoreCase) &&
                            !line.Trim().StartsWith("Total", StringComparison.OrdinalIgnoreCase) &&
                            !line.Trim().EndsWith("Total", StringComparison.OrdinalIgnoreCase) &&
                            line.IndexOf("Consolidado de", StringComparison.OrdinalIgnoreCase) < 0
                        )
                ).TrimEnd();

                var blockRawForDebug = blockLarge.TrimEnd();
                if (string.IsNullOrWhiteSpace(blockDesc)) continue;

                var mDate = RxDateLineAnchor.Match(blockDesc);
                if (!mDate.Success || mDate.Index != 0) continue;
                if (!TryParseDateDdMmYy(mDate.Groups["d"].Value, out var date)) continue;

                var detect = TightenForNumbers(blockDesc);
                detect = FixInlineMinusBeforeMoney(detect);
                detect = Regex.Replace(detect, @"(?<=\s|^)-\s{0,3}(?=\d)", "-");

                var mDelim = RxMoneyDelimited.Matches(detect);

                var strict = new List<string>(2);
                foreach (Match m in mDelim)
                {
                    var tok = m.Groups[1].Value.Trim();
                    var core = tok.Trim('-');

                    if (m.Index >= 2 && detect[m.Index - 1] == '-' && char.IsLetterOrDigit(detect[m.Index - 2]))
                    {
                        result.Warnings.Add("[dash-guard] trimmed hyphen before money near: " +
                            Trunc(detect.Substring(Math.Max(0, m.Index - 12),
                            Math.Min(24, detect.Length - Math.Max(0, m.Index - 12))), 60));
                        tok = tok.TrimStart('-');
                        dashTrimmedBeforeMoneyCount++;
                    }

                    if (!IsStrictMoney(core)) continue;

                    strict.Add(tok);
                    if (strict.Count == 2) break;
                }

                decimal amount, balance; bool usedFallbackThisBlock = false;

                // === [1] Intenta vía estricta
                if (strict.Count >= 2)
                {
                    var importeTok = strict[0];
                    var saldoTok = strict[1];

                    try
                    {
                        amount = ParseImporte(importeTok);
                        balance = ParseSaldo(saldoTok);
                    }
                    catch
                    {
                        var ab = TryParseAmountAndBalance(blockRawForDebug);
                        if (ab == null)
                            continue;
                        (amount, balance) = ab.Value; usedFallbackThisBlock = true; fallbackUsedCount++;
                    }
                }
                else
                {
                    var ab = TryParseAmountAndBalance(blockRawForDebug);
                    if (ab == null)
                        continue;
                    (amount, balance) = ab.Value; usedFallbackThisBlock = true; fallbackUsedCount++;
                }

                // === [2] NUEVO: chequeo de consistencia con prevRunningBalance;
                if (prevRunningBalance.HasValue)
                {
                    var expected = balance - prevRunningBalance.Value;
                    var closes = NearlyEq(amount, expected) || NearlyEq(-amount, expected);

                    if (!closes)
                    {
                        // Reintenta con el parser "blando" sobre el bloque RAW (sin TightenForNumbers)
                        var ab2 = TryParseAmountAndBalance(blockRawForDebug);
                        if (ab2.HasValue)
                        {
                            var (a2, b2) = ab2.Value;
                            var exp2 = b2 - prevRunningBalance.Value;
                            var closes2 = NearlyEq(a2, exp2) || NearlyEq(-a2, exp2);

                            if (closes2)
                            {
                                amount = a2;
                                balance = b2;
                                usedFallbackThisBlock = true;
                                fallbackUsedCount++;
                                result.Warnings.Add("[recovered.with.fallback] mismatch estricto; par blando cierra saldo");
                            }
                        }
                    }
                }
                
                // ======== NO RECONCILIAR ========
                // expected = balance - prevBalance (si hay prevBalance)
                bool suspicious = false;
                decimal? suggested = null;

                if (prevRunningBalance.HasValue)
                {
                    var expected = balance - prevRunningBalance.Value;

                    // si no cierra exacto (ni por flip de signo), marcar sospechoso y proponer expected
                    bool balanceMatches = NearlyEq(amount, expected);
                    bool flipMatches = NearlyEq(-amount, expected);

                    if (!balanceMatches)
                    {
                        suspicious = true;
                        suggested = expected;

                        // log minimal
                        result.Warnings.Add(
                            $"[suspect.recon] i={i + 1} prev={prevRunningBalance.Value:0.00} bal={balance:0.00} " +
                            $"amt={amount:0.00} expected={expected:0.00} flip={flipMatches}"
                        );
                    }
                }

                // Heurística semántica por descripción
                int firstAmtIdx = mDelim[0].Index;
                string mainDetect = detect.Substring(mDate.Length, Math.Max(0, firstAmtIdx - mDate.Length)).Trim();

                string displayDesc = DescriptionBuilder.BuildSmart(mainDetect ?? detect, blockRawForDebug ?? blockDesc);
                if (string.IsNullOrWhiteSpace(displayDesc))
                    displayDesc = DescriptionBuilder.Build(blockRawForDebug ?? blockDesc);
                if (string.IsNullOrWhiteSpace(displayDesc))
                    displayDesc = PostClean(mainDetect ?? detect);

                // actualizar saldo previo (para el próximo “expected”)
                prevRunningBalance = balance;

                account.Transactions ??= new List<Transaction>();
                account.Transactions.Add(new Transaction
                {
                    Date = date,
                    Description = displayDesc,
                    OriginalDescription = blockDesc,
                    Amount = amount,                      // *** NO tocamos el importe ***
                    Type = amount < 0 ? "debit" : "credit",
                    Balance = balance,
                    Category = null,
                    Subcategory = null,
                    CategorySource = null,
                    CategoryRuleId = null,

                    // === NUEVOS CAMPOS PARA LA UI ===
                    IsSuspicious = suspicious,
                    SuggestedAmount = suggested
                });

                try
                {
                    var blkLog = new
                    {
                        i = i + 1,
                        date = date.ToString("yyyy-MM-dd"),
                        cutByPage,
                        strictCount = strict.Count,
                        usedFallback = usedFallbackThisBlock,
                        amount,
                        balance,
                        suspicious,
                        suggested
                    };
                    result.Warnings.Add("[json.block] " + JsonSerializer.Serialize(blkLog));
                }
                catch { /* ignore */ }

                blocksParsedCount++;
            }

            // Periodo (preferir portada)
            var txs = account.Transactions ?? new List<Transaction>();
            if (pStart.HasValue && pEnd.HasValue)
            {
                result.Statement.PeriodStart = pStart.Value;
                result.Statement.PeriodEnd = pEnd.Value;
            }
            else if (txs.Count > 0)
            {
                result.Statement.PeriodStart = txs.Min(t => t.Date);
                result.Statement.PeriodEnd = txs.Max(t => t.Date);
            }

            if (openingP1.HasValue) account.OpeningBalance = openingP1.Value;
            if (closingP1.HasValue) account.ClosingBalance = closingP1.Value;

            if (!account.OpeningBalance.HasValue && txs.Count > 0)
                account.OpeningBalance = txs[0].Balance - txs[0].Amount;
            if (!account.ClosingBalance.HasValue && txs.Count > 0)
                account.ClosingBalance = txs[^1].Balance;

            // Chequeo suave de balances (solo warning)
            if (txs.Count > 0 && account.OpeningBalance.HasValue && account.ClosingBalance.HasValue)
            {
                var net = txs.Sum(t => t.Amount);
                var delta = account.OpeningBalance.Value + net - account.ClosingBalance.Value;
                if (Math.Abs(delta) > 0.02m)
                    result.Warnings.Add($"[balances] opening+Σ(amount) != closing (Δ={delta:0.00})");
            }

            result.Warnings.Add($"[summary.blocks] total={matches.Count} parsed={blocksParsedCount} skippedNo2={blocksSkippedNo2Count} fallbackUsed={fallbackUsedCount} pageCutRetried={pageCutRetriedCount} dashTrimmed={dashTrimmedBeforeMoneyCount}");
            return result;
        }
    }
}
