using System.Globalization;
using System.Text.RegularExpressions;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Parsers
{
    public class SupervielleStatementParser : IBankStatementParser
    {
        private static readonly bool DIAGNOSTIC = true;
        private static readonly bool DIAG_RAW_FULL = true;
        private const int RAW_CHUNK_SIZE = 900;
        private const int RAW_MAX_CHUNKS = 15; // Reducido para menos ruido

        private Action<IBankStatementParser.ProgressUpdate>? _progress;
        private void Report(string stage, int current, int total)
            => _progress?.Invoke(new IBankStatementParser.ProgressUpdate(stage, current, total));

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

        // Regex mejorados para Supervielle
        private static readonly Regex DateAtStartRx = Rx(@"^\s*(\d{2}/\d{2}/\d{2})(?!\d)", RegexOptions.Multiline);
        private static readonly Regex MoneyRx = Rx(@"-?\d{1,3}(?:\.\d{3})*,\d{2}");
        private static readonly Regex InlineDateSplitRx = Rx(@"(?<!^)(?=\d{2}/\d{2}/\d{2})");
        private static readonly Regex HeaderRx = Rx(@"^(?:Detalle\s+de\s+Movimientos\b|Saldo del período anterior\b|SALDO PERIODO ACTUAL\b|INFORMACION SOBRE EL SALDO DE SUS CUENTAS\b|TARJETA VISA\b|Acuerdos\b|Servicio\b|Los depósitos\b|PARA CONSUMIDOR\b|IMPORTANTE:|Canales de atención\b|Imp Ley\b|Le informamos\b|Monotributistas\b)", RegexOptions.IgnoreCase);
        private static readonly Regex AccountHeaderRx = Rx(@"NUMERO\s+DE\s+CUENTA\s+([0-9\-\/]+)", RegexOptions.IgnoreCase);
        private static readonly Regex MovStartRx = Rx(@"^\s*Detalle\s+de\s+Movimientos\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex DateAnchorRx = Rx(@"(?m)^\s*\d{2}/\d{2}/\d{2}(?!\d)");
        private static readonly Regex OperationNumberRx = Rx(@"Operaci[oó]n\s+\d+\s+Generada\s+el\s+\d{2}/\d{2}/\d{2}", RegexOptions.IgnoreCase);
        private static readonly Regex BalanceLineRx = Rx(@"^\s*SALDO\s+PERIODO\s+ACTUAL\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex PreviousBalanceRx = Rx(@"^\s*Saldo\s+del\s+per[ií]odo\s+anterior\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        private static readonly (Regex rx, string canon)[] CanonMap = new[]
        {
            (Rx(@"\bCRED\s+BCA\s+ELECTR\s+INTERBANC\s+EXEN\b", RegexOptions.IgnoreCase), "CREDITO INTERBANCARIO"),
            (Rx(@"\bCREDITO\s+INTERBANCARIO\b", RegexOptions.IgnoreCase), "CREDITO INTERBANCARIO"),
            (Rx(@"\bD[eé]bitos?\s+varios\b", RegexOptions.IgnoreCase), "DEBITOS VARIOS"),
            (Rx(@"\bD[eé]bito\s+por\s+Pago\s+Sueldos\b", RegexOptions.IgnoreCase), "PAGO SUELDOS"),
            (Rx(@"\bImpuesto\s+D[eé]bitos?\s+y\s+Cr[eé]ditos?/DB\b", RegexOptions.IgnoreCase), "IMPUESTO DEBITOS Y CREDITOS"),
            (Rx(@"\bDB\.?Autom-?Leasing\s+Seguros\b", RegexOptions.IgnoreCase), "DEBITO AUTOMATICO LEASING SEGUROS"),
            (Rx(@"\bDB\.?Autom-?Leasing\s+Canon\b", RegexOptions.IgnoreCase), "DEBITO AUTOMATICO LEASING CANON"),
            (Rx(@"\bEmbargo\s+Judicial\b", RegexOptions.IgnoreCase), "EMBARGO JUDICIAL"),
            (Rx(@"\bCobranzas\s+ResumenVisa\b", RegexOptions.IgnoreCase), "COBRANZAS VISA"),
            (Rx(@"\bTrf\.\s+Masivas\s+PagoProveedores\b", RegexOptions.IgnoreCase), "TRANSFERENCIA MASIVA PROVEEDORES"),
            (Rx(@"\bContras\.Ints\.Sobreg\.\b", RegexOptions.IgnoreCase), "CONTRAPARTIDA INTERESES SOBREGIRO"),
            (Rx(@"\bPago\s+Autom[aá]tico\s+de\s+Pr[eé]stamo\b", RegexOptions.IgnoreCase), "PAGO AUTOMATICO PRESTAMO"),
            (Rx(@"\bComision\s+Mantenimiento\s+Paquete\b", RegexOptions.IgnoreCase), "COMISION MANTENIMIENTO"),
            (Rx(@"\bPercepci[oó]n\s+I\.V\.A\.RG\.\s+\d+\b", RegexOptions.IgnoreCase), "PERCEPCION IVA"),
            (Rx(@"\bCobro\s+Percepci[oó]n\s+IIBB\b", RegexOptions.IgnoreCase), "PERCEPCION IIBB"),
            (Rx(@"\bIMPUESTO\s+A\s+LOS\s+SELLOS\b", RegexOptions.IgnoreCase), "IMPUESTO SELLOS"),
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
            
            // Normalización básica
            s = Regex.Replace(s, @"[ \t]+", " ").Trim();
            
            // Arreglar signos negativos pegados
            s = Regex.Replace(s, @"-\s+(?=\d)", "-");
            
            // Arreglar formato de números: 1.564.256,00
            s = Regex.Replace(s, @"(\d{1,3})(?:\.\d{3})+,(\d{2})", m => {
                var number = m.Value.Replace(".", "");
                return number;
            });
            
            // Arreglar espacios en comas decimales
            s = Regex.Replace(s, @"(?<=\d)\s*,\s*(\d{2})\b", @",$1");
            
            // Arreglar fechas fragmentadas
            s = Regex.Replace(s, @"\b(\d{2})\s*/\s*(\d{2})\s*/\s*(\d{2})\b", "$1/$2/$3");
            
            // Espacio después de fecha
            s = Regex.Replace(s, @"(?<=\d{2}/\d{2}/\d{2})(?=[A-Za-z])", " ");
            
            return s;
        }

        private static decimal ParseEsMoney(string tok, out bool isNegative)
        {
            tok = (tok ?? string.Empty).Trim();
            isNegative = tok.StartsWith("-") || tok.EndsWith("-");
            tok = tok.Trim('-').Trim();
            
            // Remover separadores de miles y convertir coma decimal
            tok = tok.Replace(".", "").Replace(" ", "");
            tok = tok.Replace(",", ".");
            
            if (!decimal.TryParse(tok, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
                throw new FormatException($"No se pudo parsear el monto: {tok}");
                
            return result;
        }

        private static string Canonicalize(string s)
        {
            var cleaned = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            foreach (var (rx, canon) in CanonMap) 
                cleaned = rx.Replace(cleaned, canon);
            
            // Limpiar referencias de operación al final
            cleaned = Regex.Replace(cleaned, @"\s*\|\s*$", "");
            cleaned = Regex.Replace(cleaned, @"\s*\d{10}\*{3}\s*$", "");
            
            return cleaned.Trim(' ', '|');
        }

        private static string ExtractMovementsRegion(string section)
        {
            var mStart = MovStartRx.Match(section);
            if (!mStart.Success) return section;

            int startIdx = mStart.Index + mStart.Length;
            
            // Buscar el final de movimientos
            var endPatterns = new[]
            {
                @"(?i)^\s*Imp\s+Ley\s+\d+",
                @"(?i)^\s*SALDO\s+PERIODO\s+ACTUAL",
                @"(?i)^\s*Los\s+dep[oó]sitos\s+en\s+pesos",
                @"(?i)^\s*Le\s+informamos\s+que"
            };

            int endIdx = section.Length;
            foreach (var pattern in endPatterns)
            {
                var m = Regex.Match(section, pattern, RegexOptions.Multiline);
                if (m.Success && m.Index > startIdx)
                {
                    endIdx = Math.Min(endIdx, m.Index);
                }
            }

            return section.Substring(startIdx, endIdx - startIdx);
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

        private static (decimal? opening, decimal? closing) ExtractAccountBalances(string sectionText)
        {
            decimal? opening = null, closing = null;

            // Buscar saldo anterior
            var prevMatch = PreviousBalanceRx.Match(sectionText);
            if (prevMatch.Success)
            {
                var prevText = prevMatch.Groups[1].Value.Trim();
                if (MoneyRx.IsMatch(prevText))
                {
                    try
                    {
                        opening = ParseEsMoney(prevText, out var neg);
                        if (neg) opening = -opening;
                    }
                    catch { /* ignorar */ }
                }
            }

            // Buscar saldo actual
            var currentMatch = BalanceLineRx.Match(sectionText);
            if (currentMatch.Success)
            {
                var currentText = currentMatch.Groups[1].Value.Trim();
                if (MoneyRx.IsMatch(currentText))
                {
                    try
                    {
                        closing = ParseEsMoney(currentText, out var neg);
                        if (neg) closing = -closing;
                    }
                    catch { /* ignorar */ }
                }
            }

            return (opening, closing);
        }

        private static void ReconcileAmountsWithRunningBalance(AccountStatement account, ParseResult result)
        {
            var txs = account.Transactions;
            if (txs == null || txs.Count == 0) return;

            for (int i = 0; i < txs.Count; i++)
            {
                decimal expectedAmount;

                if (i == 0 && account.OpeningBalance.HasValue)
                {
                    expectedAmount = txs[0].Balance - account.OpeningBalance.Value;
                }
                else if (i > 0)
                {
                    expectedAmount = txs[i].Balance - txs[i - 1].Balance;
                }
                else continue;

                // Solo corregir si hay una diferencia significativa
                if (Math.Abs(expectedAmount - txs[i].Amount) > 0.01m)
                {
                    var oldAmount = txs[i].Amount;
                    txs[i].Amount = expectedAmount;
                    txs[i].Type = expectedAmount >= 0 ? "credit" : "debit";
                    
                    if (DIAGNOSTIC)
                    {
                        result.Warnings.Add($"[amount-fix] {txs[i].Date:dd/MM/yy} '{txs[i].Description}' {oldAmount:N2} → {expectedAmount:N2}");
                    }
                }
            }
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

            EmitRawFull(result, text);

            Report("Normalizando", 1, 6);
            text = NormalizeWhole(text);

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

                var region = ExtractMovementsRegion(sectionRaw);
                var anchors = DateAnchorRx.Matches(region).Count;
                
                if (DIAGNOSTIC)
                    result.Warnings.Add($"[account] {accountNumber} - {anchors} fechas encontradas");

                // Extraer saldos de apertura y cierre
                var (opening, closing) = ExtractAccountBalances(sectionRaw);

                var rawLines = region.Replace("\r", "").Split('\n');
                var txs = new List<Transaction>();
                DateTime? currentDate = null;
                var fmts = new[] { "dd/MM/yy" };

                int i = 0, total = rawLines.Length;
                int produced = 0;

                while (i < total)
                {
                    if (i % Math.Max(1, total / 20) == 0) 
                        Report($"Cuenta {accIdx}: líneas", i, total);

                    var raw = rawLines[i] ?? string.Empty;
                    var line = NormalizeLine(raw);

                    if (line.Length == 0 || HeaderRx.IsMatch(line) || OperationNumberRx.IsMatch(line)) 
                    { 
                        i++; 
                        continue; 
                    }

                    var mDate = DateAtStartRx.Match(line);
                    if (!mDate.Success) { i++; continue; }

                    if (!DateTime.TryParseExact(mDate.Groups[1].Value, fmts, null, DateTimeStyles.None, out var d)) 
                    { 
                        i++; 
                        continue; 
                    }
                    
                    // Convertir año de 2 dígitos a 4 dígitos
                    if (d.Year < 2000) d = d.AddYears(2000);
                    currentDate = d;

                    var afterDate = line[(mDate.Groups[1].Index + mDate.Groups[1].Length)..].Trim();

                    var blockDescParts = new List<string>();
                    var allMoney = new List<string>();
                    string? nextDateRema = null;

                    // Procesar línea inicial
                    if (afterDate.Length > 0)
                    {
                        var (left, right) = SplitAtInlineDateIfAny(afterDate);
                        if (!string.IsNullOrWhiteSpace(left))
                        {
                            var moneyMatches = MoneyRx.Matches(left);
                            if (moneyMatches.Count > 0)
                            {
                                var prefix = left.Substring(0, moneyMatches[0].Index).Trim();
                                if (!string.IsNullOrWhiteSpace(prefix)) 
                                    blockDescParts.Add(prefix);
                                
                                foreach (Match m in moneyMatches) 
                                    allMoney.Add(m.Value);
                            }
                            else
                            {
                                blockDescParts.Add(left);
                            }
                        }
                        if (right != null) nextDateRema = right;
                    }

                    // Continuar leyendo líneas hasta encontrar otra fecha o suficientes montos
                    int j = i + 1;
                    while (j < total && nextDateRema == null && allMoney.Count < 2)
                    {
                        var nl = NormalizeLine(rawLines[j] ?? "");
                        if (nl.Length == 0) { j++; continue; }
                        if (HeaderRx.IsMatch(nl) || OperationNumberRx.IsMatch(nl)) { j++; continue; }
                        if (DateAtStartRx.IsMatch(nl)) break;

                        var (left, right) = SplitAtInlineDateIfAny(nl);
                        var moneyMatches = MoneyRx.Matches(left);
                        
                        if (moneyMatches.Count > 0)
                        {
                            var prefix = left.Substring(0, moneyMatches[0].Index).Trim();
                            if (!string.IsNullOrWhiteSpace(prefix)) 
                                blockDescParts.Add(prefix);
                            
                            foreach (Match m in moneyMatches) 
                                allMoney.Add(m.Value);
                        }
                        else if (!string.IsNullOrWhiteSpace(left))
                        {
                            blockDescParts.Add(left);
                        }

                        if (right != null) { nextDateRema = right; break; }
                        j++;
                    }

                    // Crear transacción si tenemos al menos 2 montos (amount + balance)
                    if (allMoney.Count >= 2)
                    {
                        try
                        {
                            var amountStr = allMoney[^2]; // Penúltimo monto = amount
                            var balanceStr = allMoney[^1]; // Último monto = balance

                            var amount = ParseEsMoney(amountStr, out var aNeg);
                            if (aNeg) amount = -amount;

                            var balance = ParseEsMoney(balanceStr, out var bNeg);
                            if (bNeg) balance = -balance;

                            var originalDesc = string.Join(" | ", DedupSegments(blockDescParts));
                            var description = Canonicalize(originalDesc);

                            // Validar que el monto sea razonable (no más de 100 millones)
                            if (Math.Abs(amount) > 100_000_000m)
                            {
                                if (DIAGNOSTIC)
                                    result.Warnings.Add($"[skip-outlier] {currentDate.Value:dd/MM/yy} amount={amount:N2} parece incorrecto");
                            }
                            else
                            {
                                txs.Add(new Transaction
                                {
                                    Date = currentDate.Value,
                                    Description = string.IsNullOrWhiteSpace(description) ? "Movimiento" : description,
                                    OriginalDescription = originalDesc,
                                    Amount = amount,
                                    Type = amount < 0 ? "debit" : "credit",
                                    Balance = balance
                                });
                                produced++;
                            }
                        }
                        catch (Exception ex)
                        {
                            if (DIAGNOSTIC)
                                result.Warnings.Add($"[parse-error] {currentDate?.ToString("dd/MM/yy") ?? "?"}: {ex.Message}");
                        }
                    }

                    i = j;

                    // Manejar fecha inline encontrada
                    if (nextDateRema != null)
                    {
                        if (total < rawLines.Length * 2) // Prevenir expansión excesiva
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
                    var account = new AccountStatement
                    {
                        AccountNumber = accountNumber ?? "",
                        Transactions = txs,
                        OpeningBalance = opening,
                        ClosingBalance = closing
                    };

                    // Reconciliar montos con balances
                    ReconcileAmountsWithRunningBalance(account, result);

                    result.Statement.Accounts.Add(account);

                    if (DIAGNOSTIC)
                    {
                        result.Warnings.Add($"[account-summary] {accountNumber}: {txs.Count} transacciones, opening={opening:N2}, closing={closing:N2}");
                    }
                }
            }

            Report("Armando cabecera", 5, 6);

            if (result.Statement.Accounts.Count > 0)
            {
                var allTxs = result.Statement.Accounts.SelectMany(a => a.Transactions);
                if (allTxs.Any())
                {
                    result.Statement.PeriodStart = allTxs.Min(t => t.Date);
                    result.Statement.PeriodEnd = allTxs.Max(t => t.Date);
                }
            }

            // Validación final de consistencia
            int inconsistentRows = 0;
            foreach (var acc in result.Statement.Accounts)
            {
                var list = acc.Transactions;
                for (int k = 1; k < list.Count; k++)
                {
                    var prev = list[k - 1];
                    var cur = list[k];
                    var expected = prev.Balance + cur.Amount;
                    if (Math.Abs(expected - cur.Balance) > 0.01m) 
                        inconsistentRows++;
                }
            }

            if (DIAGNOSTIC && inconsistentRows > 0)
                result.Warnings.Add($"[validation] {inconsistentRows} transacciones con balance inconsistente");

            Report("Listo", 6, 6);
            return result;
        }
    }
}