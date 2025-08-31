using System.Globalization;
using System.Text.RegularExpressions;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Parsers
{
    public class SupervielleStatementParser : IBankStatementParser
    {
        private static readonly bool DIAGNOSTIC = true;
        private static readonly bool DIAG_RAW_FULL = false; // Reducido para menos ruido
        private const int RAW_CHUNK_SIZE = 500;
        private const int RAW_MAX_CHUNKS = 8;

        private Action<IBankStatementParser.ProgressUpdate>? _progress;
        private void Report(string stage, int current, int total)
            => _progress?.Invoke(new IBankStatementParser.ProgressUpdate(stage, current, total));

        // Regex mejorados para Supervielle
        private static readonly Regex DateAtStartRx = new(@"^\s*(\d{2}/\d{2}/\d{2})(?!\d)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex MoneyRx = new(@"-?\d{1,3}(?:\.\d{3})*,\d{2}", RegexOptions.Compiled);
        private static readonly Regex AccountHeaderRx = new(@"NUMERO\s+DE\s+CUENTA\s+([0-9\-\/]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MovStartRx = new(@"^\s*Detalle\s+de\s+Movimientos\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex BalanceLineRx = new(@"^\s*SALDO\s+PERIODO\s+ACTUAL\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex PreviousBalanceRx = new(@"^\s*Saldo\s+del\s+per[ií]odo\s+anterior\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex CurrencyRx = new(@"22-\d{8}/(\d)\s+([U\$S]+|\$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HeaderRx = new(@"^(?:Detalle\s+de\s+Movimientos\b|Saldo del período anterior\b|SALDO PERIODO ACTUAL\b|INFORMACION SOBRE EL SALDO\b|TARJETA VISA\b|Acuerdos\b|Servicio\b|Los depósitos\b|PARA CONSUMIDOR\b|IMPORTANTE:|Canales de atención\b|Imp Ley\b|Le informamos\b|Monotributistas\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OperationNumberRx = new(@"Operaci[oó]n\s+\d+\s+Generada\s+el\s+\d{2}/\d{2}/\d{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly (Regex rx, string canon)[] CanonMap = new[]
        {
            (new Regex(@"\bCRED\s+BCA\s+ELECTR\s+INTERBANC\s+EXEN\b", RegexOptions.IgnoreCase), "CREDITO INTERBANCARIO"),
            (new Regex(@"\bD[eé]bitos?\s+varios\b", RegexOptions.IgnoreCase), "DEBITOS VARIOS"),
            (new Regex(@"\bD[eé]bito\s+por\s+Pago\s+Sueldos\b", RegexOptions.IgnoreCase), "PAGO SUELDOS"),
            (new Regex(@"\bImpuesto\s+D[eé]bitos?\s+y\s+Cr[eé]ditos?/DB\b", RegexOptions.IgnoreCase), "IMPUESTO DEBITOS Y CREDITOS"),
            (new Regex(@"\bDB\.?Autom-?Leasing\s+Seguros\b", RegexOptions.IgnoreCase), "DEBITO AUTOMATICO LEASING SEGUROS"),
            (new Regex(@"\bDB\.?Autom-?Leasing\s+Canon\b", RegexOptions.IgnoreCase), "DEBITO AUTOMATICO LEASING CANON"),
            (new Regex(@"\bEmbargo\s+Judicial\b", RegexOptions.IgnoreCase), "EMBARGO JUDICIAL"),
            (new Regex(@"\bCobranzas\s+ResumenVisa\b", RegexOptions.IgnoreCase), "COBRANZAS VISA"),
            (new Regex(@"\bTrf\.\s+Masivas\s+PagoProveedores\b", RegexOptions.IgnoreCase), "TRANSFERENCIA MASIVA PROVEEDORES"),
            (new Regex(@"\bContras\.Ints\.Sobreg\.\b", RegexOptions.IgnoreCase), "CONTRAPARTIDA INTERESES SOBREGIRO"),
            (new Regex(@"\bPago\s+Autom[aá]tico\s+de\s+Pr[eé]stamo\b", RegexOptions.IgnoreCase), "PAGO AUTOMATICO PRESTAMO"),
            (new Regex(@"\bComision\s+Mantenimiento\s+Paquete\b", RegexOptions.IgnoreCase), "COMISION MANTENIMIENTO"),
            (new Regex(@"\bPercepci[oó]n\s+I\.V\.A\.RG\.\s+\d+\b", RegexOptions.IgnoreCase), "PERCEPCION IVA"),
            (new Regex(@"\bCobro\s+Percepci[oó]n\s+IIBB\b", RegexOptions.IgnoreCase), "PERCEPCION IIBB"),
            (new Regex(@"\bIMPUESTO\s+A\s+LOS\s+SELLOS\b", RegexOptions.IgnoreCase), "IMPUESTO SELLOS"),
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
            var s = line.Trim();
            
            // Normalización básica
            s = Regex.Replace(s, @"[ \t]+", " ");
            
            // Arreglar signos negativos pegados
            s = Regex.Replace(s, @"-\s+(?=\d)", "-");
            
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

        private static (decimal? opening, decimal? closing) ExtractAccountBalances(string sectionText)
        {
            decimal? opening = null, closing = null;

            // Buscar saldo anterior
            var prevMatch = PreviousBalanceRx.Match(sectionText);
            if (prevMatch.Success)
            {
                var prevText = prevMatch.Groups[1].Value.Trim();
                var moneyMatch = MoneyRx.Match(prevText);
                if (moneyMatch.Success)
                {
                    try
                    {
                        opening = ParseEsMoney(moneyMatch.Value, out var neg);
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
                var moneyMatch = MoneyRx.Match(currentText);
                if (moneyMatch.Success)
                {
                    try
                    {
                        closing = ParseEsMoney(moneyMatch.Value, out var neg);
                        if (neg) closing = -closing;
                    }
                    catch { /* ignorar */ }
                }
            }

            return (opening, closing);
        }

        private static string DetectCurrency(string fullText, string accountNumber)
        {
            // Buscar en la sección de información de cuentas
            var currencyMatch = CurrencyRx.Match(fullText);
            if (currencyMatch.Success)
            {
                var accountSuffix = currencyMatch.Groups[1].Value;
                var currency = currencyMatch.Groups[2].Value;
                
                // Si el número de cuenta termina igual, usar esa moneda
                if (accountNumber.EndsWith("/" + accountSuffix))
                {
                    return currency.Contains("U$S") || currency.Contains("USD") ? "USD" : "ARS";
                }
            }

            // Buscar patrones específicos en el texto
            if (Regex.IsMatch(fullText, @"U\$S|USD", RegexOptions.IgnoreCase))
                return "USD";
            
            // Por defecto asumir pesos
            return "ARS";
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
                
                if (DIAGNOSTIC)
                    result.Warnings.Add($"[account] {accountNumber} - procesando movimientos");

                // Detectar moneda de la cuenta
                var currency = DetectCurrency(text, accountNumber);

                // Extraer saldos de apertura y cierre
                var (opening, closing) = ExtractAccountBalances(sectionRaw);

                var rawLines = region.Replace("\r", "").Split('\n');
                var txs = new List<Transaction>();
                var fmts = new[] { "dd/MM/yy" };

                int i = 0, total = rawLines.Length;
                int maxIterations = total * 2; // Prevenir loops infinitos
                int iterations = 0;

                while (i < total && iterations < maxIterations)
                {
                    iterations++;
                    
                    if (i % Math.Max(1, total / 10) == 0) 
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

                    var afterDate = line[(mDate.Groups[1].Index + mDate.Groups[1].Length)..].Trim();

                    // Buscar todos los montos en la línea actual
                    var moneyMatches = MoneyRx.Matches(line).Cast<Match>().ToList();
                    
                    if (moneyMatches.Count >= 2)
                    {
                        try
                        {
                            // El penúltimo monto es el amount, el último es el balance
                            var amountMatch = moneyMatches[^2];
                            var balanceMatch = moneyMatches[^1];

                            var amount = ParseEsMoney(amountMatch.Value, out var aNeg);
                            if (aNeg) amount = -amount;

                            var balance = ParseEsMoney(balanceMatch.Value, out var bNeg);
                            if (bNeg) balance = -balance;

                            // Extraer descripción (todo lo que está entre la fecha y el primer monto)
                            var descStart = mDate.Index + mDate.Length;
                            var descEnd = amountMatch.Index;
                            var description = line.Substring(descStart, descEnd - descStart).Trim();
                            description = Canonicalize(description);

                            // Validar que el monto sea razonable
                            if (Math.Abs(amount) > 1_000_000_000m)
                            {
                                if (DIAGNOSTIC)
                                    result.Warnings.Add($"[skip-outlier] {d:dd/MM/yy} amount={amount:N2} parece incorrecto");
                            }
                            else
                            {
                                txs.Add(new Transaction
                                {
                                    Date = d,
                                    Description = string.IsNullOrWhiteSpace(description) ? "Movimiento" : description,
                                    OriginalDescription = afterDate,
                                    Amount = amount,
                                    Type = amount < 0 ? "debit" : "credit",
                                    Balance = balance
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            if (DIAGNOSTIC)
                                result.Warnings.Add($"[parse-error] {d:dd/MM/yy}: {ex.Message}");
                        }
                    }

                    i++;
                }

                if (iterations >= maxIterations)
                {
                    result.Warnings.Add($"[safety] Loop terminated after {iterations} iterations to prevent infinite loop");
                }

                if (txs.Count > 0)
                {
                    var account = new AccountStatement
                    {
                        AccountNumber = $"{accountNumber} ({currency})",
                        Transactions = txs.OrderBy(t => t.Date).ToList(),
                        OpeningBalance = opening,
                        ClosingBalance = closing
                    };

                    // Reconciliar montos con balances
                    ReconcileAmountsWithRunningBalance(account, result);

                    result.Statement.Accounts.Add(account);

                    if (DIAGNOSTIC)
                    {
                        result.Warnings.Add($"[account-summary] {accountNumber} ({currency}): {txs.Count} transacciones, opening={opening:N2}, closing={closing:N2}");
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