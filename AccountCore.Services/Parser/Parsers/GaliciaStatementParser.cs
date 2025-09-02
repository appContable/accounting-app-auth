using System.Globalization;
using System.Text.RegularExpressions;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Parsers
{
    public class GaliciaStatementParser : IBankStatementParser
    {
        private static readonly bool DIAGNOSTIC = true;

        private static readonly bool DIAG_RAW_FULL = true;
        private const int RAW_CHUNK_SIZE = 900;
        private const int RAW_MAX_CHUNKS = 24;

        private static bool PatternHasAdvancedConstructs(string pattern)
            => pattern.IndexOf("(?", StringComparison.Ordinal) >= 0;

        private static Regex Rx(string pattern, RegexOptions extra = 0)
        {
            var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant | extra;
            var timeout = TimeSpan.FromSeconds(2);
#if NET7_0_OR_GREATER
            if (!PatternHasAdvancedConstructs(pattern))
            {
                try { return new Regex(pattern, opts | RegexOptions.NonBacktracking, timeout); }
                catch (NotSupportedException) { }
            }
#endif
            return new Regex(pattern, opts, timeout);
        }

        private static readonly Regex MoneyRx = Rx(@"-?\d{1,3}(?:[.\s]?\d{3})*,\s*\d{2}|-?\d+,\s*\d{2}");
        private static readonly Regex DateAtStartRx = Rx(@"^\s*(\d{2}/\d{2}/(?:\d{4}|\d{2}))(?!\d)", RegexOptions.Multiline);
        private static readonly Regex InlineDateSplitRx = Rx(@"(?<!^)(?=\d{2}/\d{2}/(?:\d{4}|\d{2}))");
        private static readonly Regex DateAnchorRx = Rx(@"(?m)^\s*\d{2}/\d{2}/(?:\d{2}|\d{4})(?!\d)");
        private static readonly Regex HeaderRx = Rx(@"^(?:P[aá]gina\b|Movimientos\b|Fecha\s+Descripci[oó]n\s+Origen\s+Cr[ée]dito\s+D[ée]bito\s+Saldo\b|Resumen\b|Saldos?\b|Total\b|Canales\b|Chate[aá]\b|BCRA\b)", RegexOptions.IgnoreCase);
        private static readonly Regex MonthLineRx = Rx(@"^\s*(?:Enero|Febrero|Marzo|Abril|Mayo|Junio|Julio|Agosto|Septiembre|Octubre|Noviembre|Diciembre)\s+\d{4}\s*$", RegexOptions.IgnoreCase);

        private static readonly (Regex rx, string canon)[] CanonMap = new[]
        {
            (Rx(@"\bPAGO\s+TARJETAMASTER\b", RegexOptions.IgnoreCase), "PAGO TARJETA MASTER"),
            (Rx(@"\bPAGO\s+TARJETAVISA\b",   RegexOptions.IgnoreCase), "PAGO TARJETA VISA"),
            (Rx(@"\bCOMPRA\s+DEBITO\b",      RegexOptions.IgnoreCase), "COMPRA DEBITO"),
            (Rx(@"\bTRANSF\.?\s*CTAS\s*PROPIAS\b", RegexOptions.IgnoreCase), "TRANSFERENCIA ENTRE CUENTAS PROPIAS"),
            (Rx(@"\bTRANSFERENCIA\s+DE\s+TERCEROS\b", RegexOptions.IgnoreCase), "TRANSFERENCIA DE TERCEROS"),
            (Rx(@"\bTRANSFERENCIA\s+DE\s+CUENTA\b",  RegexOptions.IgnoreCase), "TRANSFERENCIA DE CUENTA"),
            (Rx(@"\bD\.A\. ?AL\s*VTO\b",     RegexOptions.IgnoreCase), "D.A. AL VTO"),
        };

        // Helper: extrae el "final" del pie de tabla (Total ... $ ... $ ... $ <final>)
        private static decimal? TryExtractFinalFromTotal(string full)
        {
            var rx = Rx(@"(?im)Total\s*\$\s*[^\n$]*\$\s*[^\n$]*\$\s*(?<f>-?\d{1,3}(?:[.\s]?\d{3})*,\s*\d{2}|-?\d+,\s*\d{2})");
            var m = rx.Match(full);
            if (!m.Success) return null;
            var d = ParseEsMoney(m.Groups["f"].Value, out var neg);
            return neg ? -d : d;
        }

        // Helper: toma los dos importes del bloque "Saldos" sin asumir orden
        private static (decimal? v1, decimal? v2) TryExtractTwoFromSaldos(string full)
        {
            var rx = Rx(@"(?ims)Saldos?\b.*?(?<v1>-?\d{1,3}(?:[.\s]?\d{3})*,\s*\d{2}|-?\d+,\s*\d{2}).*?(?<v2>-?\d{1,3}(?:[.\s]?\d{3})*,\s*\d{2}|-?\d+,\s*\d{2})");
            var m = rx.Match(full);
            if (!m.Success) return (null, null);

            decimal? p1 = ParseEsMoney(m.Groups["v1"].Value, out var n1);
            decimal? p2 = ParseEsMoney(m.Groups["v2"].Value, out var n2);
            if (n1) p1 = -p1;
            if (n2) p2 = -p2;
            return (p1, p2);
        }

        // Decide apertura/cierre usando "Total final" o último balance como ancla
        private static (decimal? opening, decimal? closing) TryExtractBalancesSmart(string full, decimal? lastBalance)
        {
            var (v1, v2) = TryExtractTwoFromSaldos(full);
            if (v1 is null && v2 is null)
                return (null, null);

            // 1) Ancla con el Total del pie (más confiable)
            var finalFromTotal = TryExtractFinalFromTotal(full);
            if (finalFromTotal is not null && v1 is not null && v2 is not null)
            {
                var d1 = Math.Abs(finalFromTotal.Value - v1.Value);
                var d2 = Math.Abs(finalFromTotal.Value - v2.Value);
                if (d1 <= d2) return (v2, v1); // v1 es final ⇒ opening=v2, closing=v1
                else return (v1, v2);          // v2 es final ⇒ opening=v1, closing=v2
            }

            // 2) Si no hay "Total", anclá con el último balance de movimientos (si existe)
            if (lastBalance is not null && v1 is not null && v2 is not null)
            {
                var d1 = Math.Abs(lastBalance.Value - v1.Value);
                var d2 = Math.Abs(lastBalance.Value - v2.Value);
                if (d1 <= d2) return (v2, v1);
                else return (v1, v2);
            }

            // 3) Fallback: si solo encontraste uno, asumilo como closing
            if (v1 is not null && v2 is null) return (null, v1);
            if (v2 is not null && v1 is null) return (null, v2);

            // 4) Último fallback conservador: asumir orden típico "Inicial, Final"
            return (v1, v2);
        }

        // === NUEVO ===
        // Recalcula cada Amount a partir del balance acumulado y corrige outliers
        private static void ReconcileAmountsWithRunningBalance(AccountStatement account, ParseResult result)
        {
            var txs = account.Transactions;
            if (txs == null || txs.Count == 0) return;

            for (int i = 0; i < txs.Count; i++)
            {
                decimal inferred;

                if (i == 0 && account.OpeningBalance.HasValue)
                {
                    inferred = Math.Round(txs[0].Balance - account.OpeningBalance.Value, 2);
                }
                else if (i > 0)
                {
                    inferred = Math.Round(txs[i].Balance - txs[i - 1].Balance, 2);
                }
                else continue;

                if (Math.Abs(inferred - txs[i].Amount) > 0.01m)
                {
                    var old = txs[i].Amount;
                    txs[i].Amount = inferred;
                    txs[i].Type = inferred >= 0 ? "credit" : "debit";
                    result.Warnings.Add($"[amount-reconciled] {txs[i].Date:yyyy-MM-dd} '{txs[i].Description}' old={old} new={inferred}");
                }
            }
        }
        // === /NUEVO ===

        private static IEnumerable<string> Chunk(string s, int size)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            for (int i = 0; i < s.Length; i += size)
                yield return s.Substring(i, Math.Min(size, s.Length - i));
        }
        private static void EmitRawFull(ParseResult result, string raw)
        {
            if (!DIAG_RAW_FULL) return;
            var txt = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            int n = 0;
            foreach (var c in Chunk(txt, RAW_CHUNK_SIZE))
            {
                result.Warnings.Add($"[raw-full #{++n}] {c}");
                if (n >= RAW_MAX_CHUNKS) break;
            }
            result.Warnings.Add($"[raw-full] total_chunks={n}, total_chars={txt.Length}");
        }

        private static string NormalizeWhole(string s)
        {
            if (s == null) return string.Empty;
            s = s.Replace("\u00A0", " ").Replace("\u202F", " ");
            s = s.Replace('\u2212', '-').Replace('\u2012', '-').Replace('\u2013', '-').Replace('\u2014', '-');
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            return s;
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
            s = Regex.Replace(s, @"\b(\d)\s+(\d)\s*/\s*(\d{2})\s*/\s*(\d{2,4})\b", "$1$2/$3/$4");
            s = Regex.Replace(s, @"\b(\d{2})\s*/\s*(\d{2})\s*/\s*(\d{2,4})\b", "$1/$2/$3");
            s = Regex.Replace(s, @"(?<=\d{2}/\d{2}/(?:\d{2}|\d{4}))(?=[A-Za-z])", " ");
            return s;
        }
        private static string ExtractMovementsRegion(string full)
        {
            var startRx = Rx(@"(?ims)(^|\n)\s*(Movimientos|Fecha\s+Descripci[oó]n\s+Origen\s+Cr[ée]dito\s+D[ée]bito\s+Saldo)\s*(\n|$)");
            var mStart = startRx.Match(full);
            if (!mStart.Success) return full;
            int startIdx = mStart.Index + mStart.Length;

            var endRx = Rx(@"(?ims)(^|\n)\s*(Total\s*\$|Saldos?|Canales\s+de\s+atenci[oó]n|Chate[aá])\b");
            var mEnd = endRx.Match(full, startIdx);
            int endIdx = mEnd.Success ? mEnd.Index : full.Length;

            return full.Substring(startIdx, endIdx - startIdx);
        }
        private static string? DetectAccountNumber(string full)
        {
            var m = Regex.Match(full, @"(?i)Cuenta:\s*([0-9\- ]+)|N[º°]?\s*[: ]\s*([0-9\- ]{6,})");
            if (m.Success)
            {
                var g = m.Groups[1].Success ? m.Groups[1].Value : (m.Groups.Count > 2 && m.Groups[2].Success ? m.Groups[2].Value : null);
                return g?.Trim();
            }
            return null;
        }
        private static string CanonicalizeDescription(string s)
        {
            var cleaned = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            foreach (var (rx, canon) in CanonMap) cleaned = rx.Replace(cleaned, canon);
            cleaned = Regex.Replace(cleaned, @"\bCUENTA\s+ORIGEN\s+CAJA(?:\s+A)?\b", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s*\|\s*", " | ");
            cleaned = Regex.Replace(cleaned, @"(\s*\|\s*){2,}", " | ");
            return cleaned.Trim(' ', '|');
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
        private static decimal ParseEsMoney(string tok, out bool isNegative)
        {
            tok = (tok ?? string.Empty).Trim();
            isNegative = tok.StartsWith("-") || tok.EndsWith("-");
            tok = tok.Trim('-').Trim();
            tok = tok.Replace(".", "").Replace(" ", "");
            tok = tok.Replace(",", ".");
            return decimal.Parse(tok, CultureInfo.InvariantCulture);
        }

        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress = null)
        {
            var result = new ParseResult
            {
                Statement = new BankStatement
                {
                    Bank = "Banco Galicia",
                    Accounts = new List<AccountStatement>()
                },
                Warnings = new List<string>()
            };

            EmitRawFull(result, text);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Normalizando", 1, 6));
            text = NormalizeWhole(text);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Detectando cuenta", 2, 6));
            var accountNumber = DetectAccountNumber(text) ?? "";
            var account = new AccountStatement { AccountNumber = accountNumber };
            result.Statement.Accounts.Add(account);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Recortando movimientos", 3, 6));
            var region = ExtractMovementsRegion(text);
            var anchors = DateAnchorRx.Matches(region).Count;
            result.Warnings.Add($"[precheck] anclas_fecha={anchors}");

            var rawLines = region.Split('\n');
            var txs = account.Transactions;
            DateTime? currentDate = null;
            var fmts = new[] { "dd/MM/yyyy", "dd/MM/yy" };

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Parseando líneas", 4, 6));
            int i = 0, total = rawLines.Length, produced = 0, moneyRows = 0;

            while (i < total)
            {
                if ((i & 31) == 0) progress?.Invoke(new IBankStatementParser.ProgressUpdate("Parseando líneas", i, total));

                var raw = rawLines[i] ?? string.Empty;
                var line = NormalizeLine(raw);

                if (line.Length == 0 || HeaderRx.IsMatch(line) || MonthLineRx.IsMatch(line)) { i++; continue; }

                var mDate = DateAtStartRx.Match(line);
                if (!mDate.Success) { i++; continue; }

                if (!DateTime.TryParseExact(mDate.Groups[1].Value, fmts, null, DateTimeStyles.None, out var d)) { i++; continue; }
                currentDate = d;

                var afterDate = line[(mDate.Groups[1].Index + mDate.Groups[1].Length)..].Trim();

                var blockDescParts = new List<string>(8);
                var allMoney = new List<Match>();
                string? nextDateRema = null;

                afterDate = NormalizeLine(afterDate);
                if (afterDate.Length > 0)
                {
                    var (left, right) = SplitAtInlineDateIfAny(afterDate);
                    if (!string.IsNullOrWhiteSpace(left))
                    {
                        var mm = MoneyRx.Matches(left);
                        if (mm.Count > 0)
                        {
                            var prefix = mm[0].Index > 0 ? left[..mm[0].Index].Trim() : string.Empty;
                            if (!string.IsNullOrWhiteSpace(prefix)) blockDescParts.Add(prefix);
                            foreach (Match m in mm) allMoney.Add(m);
                        }
                        else blockDescParts.Add(left);
                    }
                    if (right != null) nextDateRema = right;
                }

                int j = i + 1;
                while (j < total && nextDateRema == null)
                {
                    var nl = NormalizeLine(rawLines[j] ?? "");
                    if (nl.Length == 0) { j++; continue; }
                    if (HeaderRx.IsMatch(nl) || MonthLineRx.IsMatch(nl)) break;
                    if (DateAtStartRx.IsMatch(nl)) break;

                    var (left, right) = SplitAtInlineDateIfAny(nl);
                    var mm2 = MoneyRx.Matches(left);
                    if (mm2.Count > 0)
                    {
                        var pre = mm2[0].Index > 0 ? left[..mm2[0].Index].Trim() : string.Empty;
                        if (!string.IsNullOrWhiteSpace(pre)) blockDescParts.Add(pre);
                        foreach (Match m in mm2) allMoney.Add(m);
                    }
                    else if (!string.IsNullOrWhiteSpace(left)) blockDescParts.Add(left);

                    if (right != null) { nextDateRema = right; break; }
                    j++;
                }

                if (allMoney.Count >= 2)
                {
                    var amountM = allMoney[^2];
                    var balanceM = allMoney[^1];

                    try
                    {
                        var amount = ParseEsMoney(amountM.Value, out var aNeg); if (aNeg) amount = -amount;
                        var balance = ParseEsMoney(balanceM.Value, out var bNeg); if (bNeg) balance = -balance;

                        var description = CanonicalizeDescription(string.Join(" | ", blockDescParts));
                        txs.Add(new Transaction
                        {
                            Date = currentDate.Value,
                            Description = string.IsNullOrWhiteSpace(description) ? "Movimiento" : description,
                            OriginalDescription = string.Join(" | ", blockDescParts),
                            Amount = amount,
                            Type = amount < 0 ? "debit" : "credit",
                            Balance = balance
                        });
                        produced++; moneyRows++;
                    }
                    catch { /* descartar bloque inválido */ }
                }

                i = j;

                if (nextDateRema != null)
                {
                    var list = rawLines.ToList();
                    list.Insert(i, nextDateRema);
                    rawLines = list.ToArray();
                    total = rawLines.Length;
                }
            }

            var lastBalance = txs.Count > 0 ? txs[^1].Balance : (decimal?)null;

            // Usa el método "smart" para saldos
            var (openingFromHeader, closingFromHeader) = TryExtractBalancesSmart(text, lastBalance);

            // Asignación con fallbacks
            decimal? closing = closingFromHeader ?? lastBalance;
            decimal? opening = openingFromHeader;

            // Si apertura no vino del header, inferila desde el primer movimiento
            if (opening is null && txs.Count > 0)
                opening = txs[0].Balance - txs[0].Amount;

            // Guardo en la cuenta
            account.OpeningBalance = opening;
            account.ClosingBalance = closing;

            // === NUEVO: reconciliar montos con running balance ===
            ReconcileAmountsWithRunningBalance(account, result);
            // === /NUEVO ===

            // (Sanity) Validar que opening + suma(amounts) ~= closing
            if (opening is not null && closing is not null && txs.Count > 0)
            {
                var net = txs.Sum(t => t.Amount);
                if (Math.Abs((opening.Value + net) - closing.Value) > 0.02m)
                    result.Warnings.Add($"[balances] Inconsistencia: opening+net != closing (Δ={(opening.Value + net - closing.Value):0.00})");
            }

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Armando cabecera", 5, 6));
            if (txs.Count > 0)
            {
                result.Statement.PeriodStart = txs.Min(t => t.Date);
                result.Statement.PeriodEnd = txs.Max(t => t.Date);
            }

            if (DIAGNOSTIC)
            {
                result.Warnings.Add($"[diag] lines={rawLines.Length}, moneyRows={moneyRows}, parsed={produced}, range={result.Statement.PeriodStart:yyyy-MM-dd}->{result.Statement.PeriodEnd:yyyy-MM-dd}");
                if (opening.HasValue && closing.HasValue)
                    result.Warnings.Add($"[balances] opening={opening.Value}, closing={closing.Value}");
            }

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Listo", 6, 6));
            return result;
        }
    }
}
