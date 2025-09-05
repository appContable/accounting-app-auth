using System.Globalization;
using System.Text.RegularExpressions;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Parsers
{
    public class GaliciaStatementParser : IBankStatementParser
    {
        private static readonly bool DIAGNOSTIC = true;

        // Regex patterns mejorados
        private static readonly Regex DateRx = new(@"^(\d{2}/\d{2}/\d{2})", RegexOptions.Compiled);
        private static readonly Regex MoneyRx = new(@"-?\d{1,3}(?:\.\d{3})*,\d{2}", RegexOptions.Compiled);
        private static readonly Regex MovimientosStartRx = new(@"^\s*Movimientos\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex TotalRx = new(@"^\s*Total\s*\$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex SaldosRx = new(@"Saldos\s*(.+?)(?=\n|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex AccountNumberRx = new(@"N[°º]?\s*(\d{7}-\d)\s*(\d{3}-\d)", RegexOptions.IgnoreCase);
        private static readonly Regex PeriodRx = new(@"(\d{2}/\d{2}/\d{4})\s*(\d{2}/\d{2}/\d{4})", RegexOptions.Compiled);

        // Canonicalization patterns específicos para Galicia
        private static readonly (Regex rx, string canon)[] CanonMap = new[]
        {
            (new Regex(@"\bTRANSFERENCIA\s+DE\s+CUENTA\s*PROPIA\b", RegexOptions.IgnoreCase), "TRANSFERENCIA ENTRE CUENTAS PROPIAS"),
            (new Regex(@"\bSERVICIO\s+ACREDITAMIENTO\s+DE\s*HABERES\b", RegexOptions.IgnoreCase), "ACREDITACION HABERES"),
            (new Regex(@"\bIMP\.\s*DEB\.\s*LEY\s*25413\s*GRAL\.\b", RegexOptions.IgnoreCase), "IMPUESTO DEBITOS LEY 25413"),
            (new Regex(@"\bCOMISION\s+SERVICIO\s+DE\s+CUENTA\b", RegexOptions.IgnoreCase), "COMISION MANTENIMIENTO CUENTA"),
            (new Regex(@"\bPERCEP\.\s*IVA\b", RegexOptions.IgnoreCase), "PERCEPCION IVA"),
            (new Regex(@"\bINTERESES\s+SOBRE\s+SALDOS\s*DEUDORES\b", RegexOptions.IgnoreCase), "INTERESES SOBREGIRO"),
            (new Regex(@"\bIMPUESTO\s+DE\s+SELLOS\b", RegexOptions.IgnoreCase), "IMPUESTO SELLOS"),
            (new Regex(@"\bPAGO\s+VISA\s+EMPRESA\b", RegexOptions.IgnoreCase), "PAGO TARJETA VISA"),
            (new Regex(@"\bTRANSFERENCIAS\s+CASH\s*PROVEEDORES\b", RegexOptions.IgnoreCase), "TRANSFERENCIA PROVEEDORES"),
            (new Regex(@"\bING\.\s*BRUTOS\s+S/\s*CRED\b", RegexOptions.IgnoreCase), "INGRESOS BRUTOS CREDITO"),
            (new Regex(@"\bIMP\.\s*CRE\.\s*LEY\s*25413\b", RegexOptions.IgnoreCase), "IMPUESTO CREDITOS LEY 25413"),
            (new Regex(@"\bDEB\.\s*AUTOM\.\s*DE\s*SERV\.\b", RegexOptions.IgnoreCase), "DEBITO AUTOMATICO"),
            (new Regex(@"\bSUSCRIPCION\s+FIMA\b", RegexOptions.IgnoreCase), "SUSCRIPCION FONDO"),
            (new Regex(@"\bRESCATE\s+FIMA\b", RegexOptions.IgnoreCase), "RESCATE FONDO"),
            (new Regex(@"\bTRF\s+INMED\s+PROVEED\b", RegexOptions.IgnoreCase), "TRANSFERENCIA INMEDIATA PROVEEDOR"),
        };

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

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Normalizando texto", 1, 6));
            text = NormalizeText(text);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Extrayendo información básica", 2, 6));
            var accountNumber = ExtractAccountNumber(text);
            var (periodStart, periodEnd) = ExtractPeriod(text);
            var (openingBalance, closingBalance) = ExtractBalances(text);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Extrayendo movimientos", 3, 6));
            var movementsText = ExtractMovementsSection(text);
            
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Parseando transacciones", 4, 6));
            var transactions = ParseTransactions(movementsText, result);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Validando consistencia", 5, 6));
            var account = new AccountStatement 
            { 
                AccountNumber = accountNumber ?? "Cuenta no detectada",
                Transactions = transactions,
                OpeningBalance = openingBalance,
                ClosingBalance = closingBalance,
                Currency = "ARS"
            };
            
            ValidateAndReconcile(account, result);
            result.Statement.Accounts.Add(account);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Finalizando", 6, 6));
            result.Statement.PeriodStart = periodStart;
            result.Statement.PeriodEnd = periodEnd;

            if (DIAGNOSTIC)
            {
                result.Warnings.Add($"[diag] parsed={transactions.Count}, opening={openingBalance:N2}, closing={closingBalance:N2}");
                result.Warnings.Add($"[diag] period={periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}");
            }

            return result;
        }

        private static string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            // Reemplazar caracteres especiales
            text = text.Replace("\u00A0", " ").Replace("\u202F", " ");
            text = text.Replace('\u2212', '-').Replace('\u2012', '-').Replace('\u2013', '-').Replace('\u2014', '-');
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');

            return text.Trim();
        }

        private static string? ExtractAccountNumber(string text)
        {
            var match = AccountNumberRx.Match(text);
            if (match.Success)
            {
                return $"{match.Groups[1].Value} {match.Groups[2].Value}";
            }

            // Fallback: buscar patrón en el texto
            var simpleMatch = Regex.Match(text, @"N[°º]?\s*(\d{7}-\d)\s*(\d{3}-\d)");
            if (simpleMatch.Success)
            {
                return $"{simpleMatch.Groups[1].Value} {simpleMatch.Groups[2].Value}";
            }

            return null;
        }

        private static (DateTime? start, DateTime? end) ExtractPeriod(string text)
        {
            var match = PeriodRx.Match(text);
            if (match.Success)
            {
                if (DateTime.TryParseExact(match.Groups[1].Value, "dd/MM/yyyy", null, DateTimeStyles.None, out var start) &&
                    DateTime.TryParseExact(match.Groups[2].Value, "dd/MM/yyyy", null, DateTimeStyles.None, out var end))
                {
                    return (start, end);
                }
            }
            return (null, null);
        }

        private static (decimal? opening, decimal? closing) ExtractBalances(text)
        {
            decimal? opening = null;
            decimal? closing = null;

            // Buscar en la sección "Saldos"
            var saldosMatch = SaldosRx.Match(text);
            if (saldosMatch.Success)
            {
                var saldosText = saldosMatch.Groups[1].Value;
                var moneyMatches = MoneyRx.Matches(saldosText);
                
                if (moneyMatches.Count >= 2)
                {
                    // Primer monto: saldo inicial, segundo monto: saldo final
                    opening = ParseMoney(moneyMatches[0].Value);
                    closing = ParseMoney(moneyMatches[1].Value);
                }
            }

            return (opening, closing);
        }

        private static string ExtractMovementsSection(string text)
        {
            var startMatch = MovimientosStartRx.Match(text);
            if (!startMatch.Success) return text;

            var startIndex = startMatch.Index + startMatch.Length;
            
            var endMatch = TotalRx.Match(text, startIndex);
            var endIndex = endMatch.Success ? endMatch.Index : text.Length;

            return text.Substring(startIndex, endIndex - startIndex).Trim();
        }

        private static List<Transaction> ParseTransactions(string movementsText, ParseResult result)
        {
            var transactions = new List<Transaction>();
            var lines = movementsText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            
            var i = 0;
            while (i < lines.Length)
            {
                var transaction = TryParseTransactionBlock(lines, ref i, result);
                if (transaction != null)
                {
                    transactions.Add(transaction);
                }
                else
                {
                    i++; // Avanzar si no se pudo parsear
                }
            }

            return transactions.OrderBy(t => t.Date).ToList();
        }

        private static Transaction? TryParseTransactionBlock(string[] lines, ref int index, ParseResult result)
        {
            if (index >= lines.Length) return null;

            var currentLine = lines[index].Trim();
            if (string.IsNullOrEmpty(currentLine)) return null;

            // Buscar fecha al inicio de la línea
            var dateMatch = DateRx.Match(currentLine);
            if (!dateMatch.Success) return null;

            // Parsear fecha
            if (!DateTime.TryParseExact(dateMatch.Groups[1].Value, "dd/MM/yy", null, DateTimeStyles.None, out var date))
                return null;

            // Convertir año de 2 dígitos a 4 dígitos (asumiendo 20xx)
            if (date.Year < 2000) date = date.AddYears(2000);

            // Recopilar todas las líneas que pertenecen a esta transacción
            var transactionLines = new List<string> { currentLine };
            var nextIndex = index + 1;

            // Continuar recopilando líneas hasta encontrar otra fecha o llegar al final
            while (nextIndex < lines.Length)
            {
                var nextLine = lines[nextIndex].Trim();
                if (string.IsNullOrEmpty(nextLine))
                {
                    nextIndex++;
                    continue;
                }

                // Si encontramos otra fecha, paramos
                if (DateRx.IsMatch(nextLine)) break;

                transactionLines.Add(nextLine);
                nextIndex++;
            }

            // Procesar el bloque completo de la transacción
            var transaction = ProcessTransactionBlock(transactionLines, date, result);
            
            // Actualizar el índice para continuar desde la siguiente transacción
            index = nextIndex - 1; // -1 porque el bucle principal hará i++

            return transaction;
        }

        private static Transaction? ProcessTransactionBlock(List<string> lines, DateTime date, ParseResult result)
        {
            // Unir todas las líneas en un solo texto
            var fullText = string.Join(" ", lines);
            
            if (DIAGNOSTIC)
            {
                result.Warnings.Add($"[block] {date:dd/MM/yy} - processing: {fullText}");
            }

            // Extraer todos los montos del bloque
            var moneyMatches = MoneyRx.Matches(fullText);
            if (moneyMatches.Count < 1)
            {
                if (DIAGNOSTIC)
                    result.Warnings.Add($"[skip] {date:dd/MM/yy} - no money amounts found");
                return null;
            }

            // Según tu análisis:
            // - El último monto es el saldo (puede tener negativo al final)
            // - Si hay 2 montos: [débito/crédito, saldo]
            // - Si hay 3 montos: [crédito, débito, saldo]

            decimal amount = 0;
            decimal balance;
            string balanceText = moneyMatches[^1].Value; // Último monto = saldo

            // Parsear saldo (puede tener negativo al final)
            balance = ParseMoney(balanceText);

            if (moneyMatches.Count == 1)
            {
                // Solo saldo, no hay monto de transacción explícito
                if (DIAGNOSTIC)
                    result.Warnings.Add($"[skip] {date:dd/MM/yy} - only balance found, no transaction amount");
                return null;
            }
            else if (moneyMatches.Count == 2)
            {
                // [monto, saldo]
                amount = ParseMoney(moneyMatches[0].Value);
            }
            else if (moneyMatches.Count >= 3)
            {
                // [crédito, débito, saldo] o múltiples montos
                // Tomar el penúltimo como monto de transacción
                amount = ParseMoney(moneyMatches[^2].Value);
            }

            // Extraer descripción (todo lo que está entre la fecha y los montos)
            var dateText = DateRx.Match(fullText).Value;
            var dateEndIndex = fullText.IndexOf(dateText) + dateText.Length;
            var firstMoneyIndex = fullText.IndexOf(moneyMatches[0].Value);
            
            var description = fullText.Substring(dateEndIndex, firstMoneyIndex - dateEndIndex).Trim();
            description = CleanDescription(description);

            // Validar que los montos sean razonables
            if (Math.Abs(amount) > 1_000_000_000m || Math.Abs(balance) > 1_000_000_000m)
            {
                if (DIAGNOSTIC)
                    result.Warnings.Add($"[skip-outlier] {date:dd/MM/yy} amount={amount:N2} balance={balance:N2} - montos excesivos");
                return null;
            }

            return new Transaction
            {
                Date = date,
                Description = string.IsNullOrWhiteSpace(description) ? "Movimiento" : description,
                OriginalDescription = fullText,
                Amount = amount,
                Type = amount < 0 ? "debit" : "credit",
                Balance = balance
            };
        }

        private static string CleanDescription(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return string.Empty;

            // Normalizar espacios
            description = Regex.Replace(description, @"\s+", " ").Trim();

            // Aplicar canonicalizaciones
            foreach (var (rx, canon) in CanonMap)
            {
                description = rx.Replace(description, canon);
            }

            // Limpiar códigos y referencias al final
            description = Regex.Replace(description, @"\s*\d{10,}\s*$", ""); // Códigos largos
            description = Regex.Replace(description, @"\s*[A-Z0-9]{10,}\s*$", ""); // Referencias alfanuméricas
            description = Regex.Replace(description, @"\s*VARIOS\d+[A-Z0-9]*\s*$", ""); // Códigos VARIOS

            return description.Trim();
        }

        private static decimal ParseMoney(string moneyText)
        {
            if (string.IsNullOrWhiteSpace(moneyText)) return 0;

            // Detectar si el negativo está al final (formato Galicia)
            var isNegativeAtEnd = moneyText.EndsWith("-");
            var isNegativeAtStart = moneyText.StartsWith("-");
            
            var cleanText = moneyText.Trim('-').Trim();
            
            // Remover separadores de miles y convertir coma decimal a punto
            cleanText = cleanText.Replace(".", "").Replace(",", ".");
            
            if (decimal.TryParse(cleanText, NumberStyles.Number, CultureInfo.InvariantCulture, out var result))
            {
                return (isNegativeAtEnd || isNegativeAtStart) ? -result : result;
            }

            throw new FormatException($"No se pudo parsear el monto: {moneyText}");
        }

        private static void ValidateAndReconcile(AccountStatement account, ParseResult result)
        {
            var transactions = account.Transactions;
            if (transactions == null || transactions.Count == 0) return;

            // Verificar consistencia de balances
            for (int i = 0; i < transactions.Count; i++)
            {
                decimal expectedAmount;

                if (i == 0 && account.OpeningBalance.HasValue)
                {
                    expectedAmount = transactions[i].Balance - account.OpeningBalance.Value;
                }
                else if (i > 0)
                {
                    expectedAmount = transactions[i].Balance - transactions[i - 1].Balance;
                }
                else continue;

                // Corregir montos si hay discrepancias significativas
                if (Math.Abs(expectedAmount - transactions[i].Amount) > 0.01m)
                {
                    var oldAmount = transactions[i].Amount;
                    transactions[i].Amount = expectedAmount;
                    transactions[i].Type = expectedAmount < 0 ? "debit" : "credit";
                    
                    if (DIAGNOSTIC)
                    {
                        result.Warnings.Add($"[amount-fix] {transactions[i].Date:dd/MM/yy} '{transactions[i].Description}' {oldAmount:N2} → {expectedAmount:N2}");
                    }
                }
            }

            // Validar balance final
            if (account.OpeningBalance.HasValue && account.ClosingBalance.HasValue && transactions.Count > 0)
            {
                var calculatedBalance = account.OpeningBalance.Value + transactions.Sum(t => t.Amount);
                var difference = Math.Abs(calculatedBalance - account.ClosingBalance.Value);
                
                if (difference > 0.02m)
                {
                    result.Warnings.Add($"[balance-mismatch] Diferencia: {difference:N2} (calculado: {calculatedBalance:N2}, esperado: {account.ClosingBalance.Value:N2})");
                }
            }
        }
    }
}