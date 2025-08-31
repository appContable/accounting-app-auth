using System.Globalization;
using System.Text.RegularExpressions;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Parsers
{
    public class SupervielleStatementParser : IBankStatementParser
    {
        private static readonly bool DIAG_FIND_TERMS = false;
        private static readonly bool DIAG_RAW_SAMPLE = false;
        private static readonly bool DIAG_RAW_FULL = true;
        private const int RAW_CHUNK_SIZE = 900;
        private const int RAW_MAX_FIND_HITS_PER_TERM = 30;

        private Action<IBankStatementParser.ProgressUpdate>? _progress;
        private void Report(string stage, int current, int total)
            => _progress?.Invoke(new IBankStatementParser.ProgressUpdate(stage, current, total));

        private static bool HasAdvanced(string pattern) =>
            pattern.IndexOf("(?", StringComparison.Ordinal) >= 0;

        private static Regex Rx(string pattern, RegexOptions extra = 0)
        {
            var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant | extra;
            var timeout = TimeSpan.FromSeconds(1);
#if NET7_0_OR_GREATER
            if (!HasAdvanced(pattern))
            {
                try { return new Regex(pattern, opts | RegexOptions.NonBacktracking, timeout); }
                catch (NotSupportedException) { }
            }
#endif
            return new Regex(pattern, opts, timeout);
        }

        private static readonly Regex DateAtStartRx = Rx(@"^\s*(\d{2}/\d{2}/(?:\d{2}|\d{4}))(?!\d)", RegexOptions.Multiline);
        private static readonly Regex QuickDateRx   = Rx(@"^\s*(?:\d{1,2}|\d\s+\d)\s*/\s*\d{2}\s*/\s*(?:\d{2}|\d{4})", RegexOptions.Multiline);
        private static readonly Regex InlineDateSplitRx = Rx(@"(?<!^)(?=\d{2}/\d{2}/(?:\d{2}|\d{4}))");
        private static readonly Regex MoneyRx       = Rx(@"-?\d{1,3}(?:[.\s]?\d{3})*,\s*\d{2}|-?\d+,\s*\d{2}");
        private static readonly Regex HeaderRx      = Rx(@"^(?:Detalle\s+de\s+Movimientos\b|Saldo del período anterior\b|SALDO PERIODO ACTUAL\b|INFORMACION SOBRE EL SALDO DE SUS CUENTAS\b|TARJETA VISA\b|Acuerdos\b|Servicio\b|Los depósitos\b|PARA CONSUMIDOR\b|IMPORTANTE:|Canales de atención\b)", RegexOptions.IgnoreCase);
        private static readonly Regex AccountHeaderRx = Rx(@"NUMERO\s+DE\s+CUENTA\s+([0-9\-\/]+)", RegexOptions.IgnoreCase);
        private static readonly Regex MovStartRx    = Rx(@"^\s*Detalle\s+de\s+Movimientos\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex DateAnchorRx  = Rx(@"(?m)^\s*\d{2}/\d{2}/(?:\d{2}|\d{4})(?!\d)");

        private static readonly (Regex rx, string canon)[] CanonMap = new[]
        {
            (Rx(@"\bCRED\s+BCA\s+ELECTR\s+INTERBANC\s+EXEN\b", RegexOptions.IgnoreCase), "CREDITO INTERBANCARIO"),
            (Rx(@"\bCREDITO\s+INTERBANCARIO\b", RegexOptions.IgnoreCase), "CREDITO INTERBANCARIO"),
            (Rx(@"\bDébitos?\s+varios\b", RegexOptions.IgnoreCase), "DEBITOS VARIOS"),
            (Rx(@"\bDébito\s+por\s+Pago\s+Sueldos\b", RegexOptions.IgnoreCase), "PAGO SUELDOS"),
            (Rx(@"\bImpuesto\s+Débitos?\s+y\s+Créditos?/DB\b", RegexOptions.IgnoreCase), "IMPUESTO DEBITOS Y CREDITOS (DB)"),
            (Rx(@"\bDB\.?\.?Autom-?Leasing\s+Seguros\b", RegexOptions.IgnoreCase), "DEBITO AUTOMATICO LEASING SEGUROS"),
            (Rx(@"\bDB\.?\.?Autom-?Leasing\s+Canon\b", RegexOptions.IgnoreCase), "DEBITO AUTOMATICO LEASING CANON"),
            (Rx(@"\bEmbargo\s+Judicial\b", RegexOptions.IgnoreCase), "EMBARGO JUDICIAL"),
            (Rx(@"\bCobranzas\s+ResumenVisa\b", RegexOptions.IgnoreCase), "COBRANZAS VISA"),
            (Rx(@"\bTrf\.\s+Masivas\s+PagoProveedores\b", RegexOptions.IgnoreCase), "TRANSFERENCIA MASIVA PROVEEDORES"),
        };

        private static string NormalizeWhole(string s)
        {
            if (s == null) return string.Empty;
            s = s.Replace("\u00A0", " ").Replace("\u202F", " ");
            s = s.Replace('\u2212', '-').Replace('\u2012', '-').Replace('\u2013', '-').Replace('\u2014', '-');
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            return s;
        }
        private static string Preprocess(string t)
        {
            t = t.Replace("\f", "\n");
            t = Regex.Replace(t, @"(?i)(NUMERO\s+DE\s+CUENTA)", "\n$1 ");
            t = Regex.Replace(t, @"(?i)(Detalle\s+de\s+Movimientos)", "\n$1\n");
            return t.Trim();
        }
        private static string NormalizeLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;
            var s = line;
            s = Regex.Replace(s, @"[ \t]+", " ").Trim();
            s = Regex.Replace(s, @"-\s+(?=\d)", "-");
            s = Regex.Replace(s, @"(\d{1,3})\s*\.\s*(\d{3},\s*\d{2})", "$1.$2");
            s = Regex.Replace(s, @"(?<=\d)\s+,", ",");
            s = Regex.Replace(s, @",\s*(\d{2})\b", @",$1");
            s = Regex.Replace(s, @",\s*(\d)\s+(\d)\b", @",$1$2");
            s = Regex.Replace(s, @"(?<=-?\d)\s+(?=\d[\d.\s]*,\s*\d{2}\b)", "");
            s = Regex.Replace(s, @"(?<=\.)\s+(?=\d{3}(?:[^\d]|$))", "");
            s = Regex.Replace(s, @"\b(\d)\s+(\d)\s*/\s*(\d{2})\s*/\s*(\d{2,4})\b", "$1$2/$3/$4");
            s = Regex.Replace(s, @"\b(\d{2})\s*/\s*(\d{2})\s*/\s*(\d{2,4})\b", "$1/$2/$3");
            s = Regex.Replace(s, @"(?<=\d{2}/\d{2}/(?:\d{2}|\d{4}))(?=[A-Za-z])", " ");
            return s;
        }
        private static decimal ParseEsMoney(string tok, out bool isNegative)
        {
            tok = (tok ?? string.Empty).Trim();
            isNegative = tok.StartsWith("-") || tok.EndsWith("-");
            tok = tok.Trim('-').Trim();
            tok = tok.Replace(".", "").Replace(" ", "");
            tok = tok.Replace(",", ".");
            return decimal.Parse(tok, CultureInfo.InvariantCulture);
        }
        private static string Canonicalize(string s)
        {
            var cleaned = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            foreach (var (rx, canon) in CanonMap) cleaned = rx.Replace(cleaned, canon);
            cleaned = Regex.Replace(cleaned, @"\s*\|\s*", " | ");
            cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
            cleaned = Regex.Replace(cleaned, @"(\s*\|\s*){2,}", " | ");
            return cleaned.Trim(' ', '|');
        }
        private static IEnumerable<string> DedupSegments(IEnumerable<string> parts)
        {
            string? prev = null;
            foreach (var p in parts)
            {
                var cur = (p ?? string.Empty).Trim();
                if (cur.Length == 0) continue;
                if (prev == null || !string.Equals(prev, cur, StringComparison.OrdinalIgnoreCase))
                    yield return cur;
                prev = cur;
            }
        }
        private static string ExtractMovementsRegion(string section)
        {
            var mStart = MovStartRx.Match(section);
            if (!mStart.Success) return section;

            int startIdx = mStart.Index + mStart.Length;
            var region = section.Substring(startIdx);

            int moneyLines = 0;
            foreach (var ln in region.Split('\n'))
            {
                var lnFix = NormalizeLine(ln);
                if (MoneyRx.Matches(lnFix).Count >= 2) { moneyLines++; if (moneyLines >= 4) break; }
            }
            return moneyLines >= 4 ? region : section;
        }
        private static string RightWindow(string s, int max = 160)
            => s.Length > max ? s[^max..] : s;

        private static IEnumerable<string> Chunk(string s, int size)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            for (int i = 0; i < s.Length; i += size)
                yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
        private static void EmitFindToWarnings(ParseResult result, string raw, params string[] terms)
        {
            if (!DIAG_FIND_TERMS || terms == null || terms.Length == 0) return;
            var lines = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var t in terms)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (!counters.TryGetValue(t, out var n)) n = 0;
                    if (n >= RAW_MAX_FIND_HITS_PER_TERM) continue;

                    if (lines[i].IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var prev = i > 0 ? lines[i - 1] : "";
                        var next = i + 1 < lines.Length ? lines[i + 1] : "";
                        result.Warnings.Add($"[find '{t}' L{i}] {prev}");
                        result.Warnings.Add($"[find '{t}' L{i}] >>> {lines[i]}");
                        result.Warnings.Add($"[find '{t}' L{i}] {next}");
                        counters[t] = n + 1;
                    }
                }
            }
        }
        private static void EmitRawSample(ParseResult result, string raw, int maxChunks = 8)
        {
            if (!DIAG_RAW_SAMPLE) return;
            int idx = 0;
            foreach (var c in Chunk(raw, RAW_CHUNK_SIZE))
            {
                if (idx >= maxChunks) break;
                result.Warnings.Add($"[raw-sample #{++idx}] {c}");
            }
            result.Warnings.Add($"[raw] sample_chunks={Math.Min(idx, maxChunks)}, total_chars={raw?.Length ?? 0}");
        }
        private static void EmitRawFull(ParseResult result, string raw)
        {
            if (!DIAG_RAW_FULL) return;
            int idx = 0;
            foreach (var c in Chunk(raw, RAW_CHUNK_SIZE))
                result.Warnings.Add($"[raw-full #{++idx}] {c}");
            result.Warnings.Add($"[raw-full] total_chunks={idx}, total_chars={raw?.Length ?? 0}");
        }

        private static List<(string account, string section)> SplitByAccounts(string full)
        {
            var matches = AccountHeaderRx.Matches(full);
            if (matches.Count == 0) return new List<(string, string)> { ("", full) };

            var res = new List<(string, string)>(matches.Count);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int start = m.Index;
                int end = (i + 1 < matches.Count) ? matches[i + 1].Index : full.Length;
                var slice = full.Substring(start, end - start);
                var acc = m.Groups[1].Value.Trim();
                res.Add((acc, slice));
            }
            return res;
        }

        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress = null)
        {
            _progress = progress;

            var result = new ParseResult
            {
                Statement = new BankStatement
                {
                    Bank = "Banco Supervielle",
                    Accounts = new List<AccountStatement>()
                },
                Warnings = new List<string>()
            };

            Report("Normalizando", 1, 6);
            text = NormalizeWhole(text);

            EmitFindToWarnings(result, text, "NUMERO DE CUENTA", "Detalle de Movimientos", "Impuesto Débitos", "Trf. Masivas", "CREDITO INTERBANCARIO");
            EmitRawSample(result, text);
            EmitRawFull(result, text);

            Report("Preprocesando", 2, 6);
            text = Preprocess(text);

            Report("Detectando cuentas", 3, 6);
            var accounts = SplitByAccounts(text);

            Report("Parseando movimientos", 4, 6);
            int accIdx = 0;
            foreach (var (accountNumber, sectionRaw) in accounts)
            {
                accIdx++;
                Report($"Cuenta {accIdx}/{accounts.Count}", accIdx, accounts.Count);

                var region  = ExtractMovementsRegion(sectionRaw);
                var anchors = DateAnchorRx.Matches(region).Count;
                result.Warnings.Add($"[precheck] {accountNumber} anclas_fecha={anchors}");

                var rawLines = region.Replace("\r", "").Split('\n');

                var txs = new List<Transaction>(64);
                DateTime? currentDate = null;
                var fmts = new[] { "dd/MM/yy", "dd/MM/yyyy" };

                int i = 0, total = rawLines.Length;
                int produced = 0, moneyRows = 0;

                // Safety counter to prevent infinite loops
                int maxIterations = total * 2; // Allow some expansion but prevent runaway
                int iterationCount = 0;

                List<Match> monies = new(8);
                static void PushMoney(List<Match> bag, Match m, int keep = 4)
                {
                    bag.Add(m);
                    if (bag.Count > keep) bag.RemoveAt(0);
                }

                while (i < total)
                {
                    // Safety check to prevent infinite loops
                    if (++iterationCount > maxIterations)
                    {
                        result.Warnings.Add($"[safety] Loop terminated after {maxIterations} iterations to prevent infinite loop");
                        break;
                    }

                    if (i % Math.Max(1, total / 50) == 0) Report($"Cuenta {accIdx}: líneas", i, total);

                    var raw = rawLines[i] ?? string.Empty;

                    if (!QuickDateRx.IsMatch(raw)) { i++; continue; }

                    var line = NormalizeLine(raw);
                    if (line.Length == 0 || HeaderRx.IsMatch(line)) { i++; continue; }

                    var mDate = DateAtStartRx.Match(line);
                    if (!mDate.Success) { i++; continue; }

                    if (!DateTime.TryParseExact(mDate.Groups[1].Value, fmts, null, DateTimeStyles.None, out var d)) { i++; continue; }
                    currentDate = d;

                    var afterDate = line[(mDate.Groups[1].Index + mDate.Groups[1].Length)..].Trim();

                    var blockDescParts = new List<string>(8);
                    monies.Clear();
                    string? nextDateRema = null;

                    afterDate = NormalizeLine(afterDate);
                    if (afterDate.Length > 0)
                    {
                        var (left, right) = SplitAtInlineDateIfAny(afterDate);
                        if (!string.IsNullOrWhiteSpace(left))
                        {
                            var mm = MoneyRx.Matches(RightWindow(left));
                            if (mm.Count > 0)
                            {
                                var prefix = mm[0].Index > 0 ? left[..mm[0].Index].Trim() : string.Empty;
                                if (!string.IsNullOrWhiteSpace(prefix)) blockDescParts.Add(prefix);
                                foreach (Match m in mm) PushMoney(monies, m);
                            }
                            else blockDescParts.Add(left);
                        }
                        if (right != null) nextDateRema = right;
                    }

                    int j = i + 1;
                    while (j < total && nextDateRema == null)
                    {
                        var nlRaw = rawLines[j] ?? string.Empty;

                        if (QuickDateRx.IsMatch(nlRaw)) break;

                        var nl = NormalizeLine(nlRaw);
                        if (nl.Length == 0) { j++; continue; }
                        if (HeaderRx.IsMatch(nl)) break;

                        var (left, right) = SplitAtInlineDateIfAny(nl);
                        var mm2 = MoneyRx.Matches(RightWindow(left));
                        if (mm2.Count > 0)
                        {
                            var pre = mm2[0].Index > 0 ? left[..mm2[0].Index].Trim() : string.Empty;
                            if (!string.IsNullOrWhiteSpace(pre)) blockDescParts.Add(pre);
                            foreach (Match m in mm2) PushMoney(monies, m);
                        }
                        else
                        {
                            if (!string.IsNullOrWhiteSpace(left)) blockDescParts.Add(left);
                        }

                        if (right != null) { nextDateRema = right; break; }
                        j++;
                    }

                    if (monies.Count >= 2)
                    {
                        var amountM = monies[^2];
                        var balanceM = monies[^1];

                        try
                        {
                            var amount = ParseEsMoney(amountM.Value, out var aNeg);
                            if (aNeg) amount = -amount;

                            var balance = ParseEsMoney(balanceM.Value, out var bNeg);
                            if (bNeg) balance = -balance;

                            var originalDesc = string.Join(" | ", DedupSegments(blockDescParts));
                            var description  = Canonicalize(originalDesc);

                            txs.Add(new Transaction
                            {
                                Date                = currentDate.Value,
                                Description         = description,
                                OriginalDescription = originalDesc,
                                Amount              = amount,
                                Type                = amount < 0 ? "debit" : "credit",
                                Balance             = balance
                            });
                            produced++; moneyRows++;
                        }
                        catch { /* ignorar */ }
                    }

                    i = j;

                    if (nextDateRema != null)
                    {
                        // Prevent infinite expansion of the array
                        if (total > rawLines.Length * 3)
                        {
                            result.Warnings.Add($"[safety] Array expansion limit reached, skipping remaining inline dates");
                            nextDateRema = null;
                        }
                        else
                        {
                        var list = rawLines.ToList();
                        list.Insert(i, nextDateRema);
                        rawLines = list.ToArray();
                        total = rawLines.Length;
                        }
                    }
                }

                if (txs.Count > 0)
                {
                    result.Statement.Accounts.Add(new AccountStatement
                    {
                        AccountNumber = accountNumber ?? "",
                        Transactions = txs
                    });
                }
            }

            Report("Armando cabecera", 5, 6);

            if (result.Statement.Accounts.Count > 0)
            {
                var all = result.Statement.Accounts.SelectMany(a => a.Transactions);
                if (all.Any())
                {
                    result.Statement.PeriodStart = all.Min(t => t.Date);
                    result.Statement.PeriodEnd   = all.Max(t => t.Date);
                }
            }

            int bad = 0;
            foreach (var acc in result.Statement.Accounts)
            {
                var list = acc.Transactions;
                for (int k = 1; k < list.Count; k++)
                {
                    var prev = list[k - 1];
                    var cur = list[k];
                    var expected = prev.Balance + cur.Amount;
                    if (Math.Abs(expected - cur.Balance) > 0.01m) bad++;
                }
            }
            if (bad > 0) result.Warnings.Add($"[ledger] {bad} filas con saldo no consistente (tol. 0,01).");

            Report("Listo", 6, 6);
            return result;
        }

        private static (string left, string? right) SplitAtInlineDateIfAny(string s)
        {
            var m = InlineDateSplitRx.Match(s);
            if (m.Success)
            {
                var left = s[..m.Index].TrimEnd();
                var right = s[m.Index..].TrimStart();
                return (left, right.Length > 0 ? right : null);
            }
            return (s, null);
        }
    }
}
