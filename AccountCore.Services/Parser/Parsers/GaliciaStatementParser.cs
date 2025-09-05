using System.Globalization;
using System.Text.RegularExpressions;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Parsers
{
    public class GaliciaStatementParser : IBankStatementParser
    {
        private static readonly bool DIAGNOSTIC = true;

        // Regex patterns
        private static readonly Regex DateRx = new(@"^(\d{2}/\d{2}/\d{2})", RegexOptions.Compiled);
        private static readonly Regex MoneyRx = new(@"-?\d{1,3}(?:\.\d{3})*,\d{2}", RegexOptions.Compiled);
        private static readonly Regex MovimientosStartRx = new(@"^\s*Movimientos\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex TotalRx = new(@"^\s*Total\s*\$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private static readonly Regex SaldosRx = new(@"Saldos(.+?)(?=\n|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex AccountNumberRx = new(@"N[°º]?\s*(\d{7}-\d)\s*(\d{3}-\d)", RegexOptions.IgnoreCase);
        private static readonly Regex PeriodRx = new(@"(\d{2}/\d{2}/\d{4})\s*(\d{2}/\d{2}/\d{4})", RegexOptions.Compiled);

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

            if (string.IsNullOrWhiteSpace(text))
                return result;

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Normalizando texto", 1, 5));
            text = NormalizeText(text);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Extrayendo información básica", 2, 5));
            var accountNumber = ExtractAccountNumber(text);
            var (periodStart, periodEnd) = ExtractPeriod(text);
            var (openingBalance, closingBalance) = ExtractBalances(text);

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Extrayendo movimientos", 3, 5));
            var movementsText = ExtractMovementsSection(text);
            
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Parseando transacciones", 4, 5));
            var transactions = ParseTransactions(movementsText, result);

            var account = new AccountStatement 
            { 
                AccountNumber = accountNumber ?? "Cuenta no detectada",
                Transactions = transactions,
                OpeningBalance = openingBalance,
                ClosingBalance = closingBalance,
                Currency = "ARS"
            };
            
            result.Statement.Accounts.Add(account);
            result.Statement.PeriodStart = periodStart;
            result.Statement.PeriodEnd = periodEnd;

            progress?.Invoke(new IBankStatementParser.ProgressUpdate("Finalizando", 5, 5));

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

        private static (decimal? opening, decimal? closing) ExtractBalances(string text)
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
                var transactionLines = CollectTransactionLines(lines, ref i);
                if (transactionLines.Count > 0)
                {
                    var transaction = ParseSingleTransaction(transactionLines, result);
                    if (transaction != null)
                    {
                        transactions.Add(transaction);
                    }
                }
            }

            return transactions.OrderBy(t => t.Date).ToList();
        }

        private static List<string> CollectTransactionLines(string[] lines, ref int index)
        {
            var transactionLines = new List<string>();
            
            if (index >= lines.Length)
                return transactionLines;

            // La primera línea debe tener una fecha
            var firstLine = lines[index].Trim();
            if (!DateRx.IsMatch(firstLine))
            {
                index++;
                return transactionLines;
            }

            transactionLines.Add(firstLine);
            index++;

            // Continuar recopilando líneas hasta encontrar otra fecha o llegar al final
            while (index < lines.Length)
            {
                var nextLine = lines[index].Trim();
                if (string.IsNullOrEmpty(nextLine))
                {
                    index++;
                    continue;
                }

                // Si encontramos otra fecha, paramos (no incrementamos index)
                if (DateRx.IsMatch(nextLine))
                    break;

                transactionLines.Add(nextLine);
                index++;
            }

            return transactionLines;
        }

        private static Transaction? ParseSingleTransaction(List<string> lines, ParseResult result)
        {
            if (lines.Count == 0) return null;

            // Unir todas las líneas en un solo texto
            var fullText = string.Join(" ", lines);
            
            // Extraer fecha de la primera línea
            var dateMatch = DateRx.Match(lines[0]);
            if (!dateMatch.Success) return null;

            if (!DateTime.TryParseExact(dateMatch.Groups[1].Value, "dd/MM/yy", null, DateTimeStyles.None, out var date))
                return null;

            // Convertir año de 2 dígitos a 4 dígitos (asumiendo 20xx)
            if (date.Year < 2000) date = date.AddYears(2000);

            if (DIAGNOSTIC)
            {
                result.Warnings.Add($"[block] {date:dd/MM/yy} - processing: {fullText}");
            }

            // Extraer todos los montos del texto completo
            var moneyMatches = MoneyRx.Matches(fullText);
            if (moneyMatches.Count < 2)
            {
                if (DIAGNOSTIC)
                    result.Warnings.Add($"[skip] {date:dd/MM/yy} - expected 2 amounts, found {moneyMatches.Count}");
                return null;
            }

            // Según tu análisis: cada línea tiene exactamente 2 montos
            // [débito/crédito, saldo]
            var amountText = moneyMatches[0].Value;
            var balanceText = moneyMatches[1].Value;

            var amount = ParseMoney(amountText);
            var balance = ParseMoney(balanceText);

            // Extraer descripción (todo lo que está entre la fecha y el primer monto)
            var dateText = dateMatch.Value;
            var dateEndIndex = fullText.IndexOf(dateText) + dateText.Length;
            var firstMoneyIndex = fullText.IndexOf(amountText);
            
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
    }
}