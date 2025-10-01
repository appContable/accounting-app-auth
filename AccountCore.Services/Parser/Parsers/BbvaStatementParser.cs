using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using AccountCore.DAL.Parser.Models;          // ParseResult, BankStatement, AccountStatement, Transaction
using AccountCore.Services.Parser.Interfaces; // IBankStatementParser

namespace AccountCore.Services.Parser.Parsers
{
    /// <summary>
    /// Parser BBVA (Argentina) para texto OCR con separadores "@@@".
    /// Reglas duras:
    /// - Cuentas: "CC $ 195-007592/8" o "CC U$S 195-000071/9" (tolerando espacios OCR).
    /// - Bloques de movimientos: detectar "MOVIMIENTOS EN CUENTAS" (título) y/o cabecera
    ///   "FECHA ORIGEN CONCEPTO DÉBITO CRÉDITO SALDO" (por página / por bloque).
    ///   * Apertura: "SALDO ANTERIOR {importe}".
    ///   * Cierre: "SALDO AL {dd} DE {MES} {importe}".
    /// - Línea de movimiento válida:
    ///   * EXACTAMENTE 2 importes latinos: 1º = movimiento, 2º = saldo (formato duro *.***,** con signo opcional; tolera espacios internos de OCR).
    ///   * 1 fecha dd/MM DESPUÉS del 2º importe (aunque esté pegada).
    ///   * Origen tras la fecha opcional ("", "D", "D123" o "123"; se descarta).
    ///   * Descripción: resto, con letras y dígitos compactados (evita falsos importes).
    /// - Filtros de saldo por moneda: ARS ≤ 1e12, USD ≤ 1e7.
    /// - PeriodStart/PeriodEnd: min/max de fechas; PeriodEnd también por "SALDO AL ...".
    /// - Multi-página: el título/cabecera puede repetirse por página/bloque.
    /// - IsSuspicious: por contabilidad (prev + amount ≈ balance, tol 0,01).
    /// </summary>
    public class BbvaStatementParser : IBankStatementParser
    {
        private static readonly CultureInfo EsAr = new("es-AR");

        // ===================== Normalización & utilitarios =====================
        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            var norm = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in norm)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string StripControls(string s) => Regex.Replace(s ?? string.Empty, @"\p{C}+", " ");

        // Compacta: sin espacios, sin tildes, MAYÚSCULAS
        private static string NormCompact(string s) =>
            Regex.Replace(RemoveDiacritics(s ?? string.Empty), @"\s+", "").ToUpperInvariant();

        private static bool IsDecor(string s) => Regex.IsMatch(s ?? string.Empty, @"^\s*([-–—]|\d)?\s*$");

        // Condensa letras separadas por un espacio (L E Y -> LEY) y colapsa espacios múltiples.
        private static string CondenseSpacedLetters(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            var noInnerSpaces = Regex.Replace(text, @"(?<=\p{L})\s(?=\p{L})", "");
            return Regex.Replace(noInnerSpaces, @"\s{2,}", " ").Trim();
        }

        // Compacta dígitos separados (2 7 2 4 -> 2724)
        private static string CompactDigits(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;
            var t = Regex.Replace(text, @"(?<=\d)\s+(?=\d)", "");
            t = Regex.Replace(t, @"\s{2,}", " ").Trim();
            return t;
        }

        // Normaliza un número LATAM con espacios internos en dígitos y separadores.
        private static string NormalizeAmountToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token ?? string.Empty;
            var t = token.Trim();
            t = Regex.Replace(t, @"^([+-])\s+(?=\d)", "$1"); // "- 568,64" -> "-568,64"
            t = Regex.Replace(t, @"\s*\.\s*", ".");
            t = Regex.Replace(t, @"\s*,\s*", ",");
            t = Regex.Replace(t, @"(?<=\d)\s+(?=\d)", "");
            return t;
        }

        // Normaliza una fecha "dd / MM" -> "dd/MM"
        private static string NormalizeDdMm(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return token ?? string.Empty;
            var t = token.Trim();
            t = Regex.Replace(t, @"\s*/\s*", "/");
            t = Regex.Replace(t, @"(?<=\d)\s+(?=\d)", "");
            return t;
        }

        private static bool ContainsHeaderMovs(string s) =>
            NormCompact(s).Contains("FECHAORIGENCONCEPTODEBITOCREDITOSALDO");

        private static bool IsMovTitle(string s) =>
            NormCompact(s).Contains("MOVIMIENTOSENCuentas"); // tolerante al OCR

        // ===================== Regex clave (tolerantes a espacios internos) =====================
        // Monto LATAM con espacios internos (ej: "- 2 3 1 . 4 0 4 , 1 7")
        private static readonly Regex RxAmountSpaced = new(
            @"[+-]?\s*(?:\d\s*){1,3}(?:\.\s*(?:\d\s*){3})*\s*,\s*\d\s*\d",
            RegexOptions.Compiled);

        // Fecha dd/MM con espacios internos
        private static readonly Regex RxDdMmAny = new(
            @"(?<dd>\d\s*\d)\s*/\s*(?<mm>\d\s*\d)",
            RegexOptions.Compiled);

        // Origen tras la fecha
        private static readonly Regex RxOriginAfterDate = new(
            @"^\s*(?:([A-Za-z])(\s*\d{3})?|\d{3})\b",
            RegexOptions.Compiled);

        // "SALDO AL 30 DE MAYO"
        private static readonly Dictionary<string, int> MonthEs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENERO"]=1, ["FEBRERO"]=2, ["MARZO"]=3, ["ABRIL"]=4, ["MAYO"]=5, ["JUNIO"]=6,
            ["JULIO"]=7, ["AGOSTO"]=8, ["SEPTIEMBRE"]=9, ["SETIEMBRE"]=9, ["OCTUBRE"]=10, ["NOVIEMBRE"]=11, ["DICIEMBRE"]=12
        };

        private sealed record CuentaCtx(string Account, string Currency);
        private sealed record TxRaw(string Line, decimal Amount, decimal Balance, DateTime Date, string Description);

        private static CuentaCtx? TryParseAccountLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            var compact = NormCompact(line);

            int idx = compact.IndexOf("CC$", StringComparison.Ordinal);
            string currency = "ARS";
            if (idx < 0)
            {
                idx = compact.IndexOf("CCU$S", StringComparison.Ordinal);
                if (idx >= 0) currency = "USD";
            }
            if (idx < 0) return null;

            int start = idx + (currency == "USD" ? "CCU$S".Length : "CC$".Length);
            if (start >= compact.Length) return null;

            var seg = compact[start..];
            var m = Regex.Match(seg, @"(?<acc>\d{3}-\d{6}/\d)");
            if (!m.Success) return null;

            var acc = m.Groups["acc"].Value;
            return new CuentaCtx(acc, currency);
        }

        private static bool TryParseOpeningBalance(string line, out decimal opening)
        {
            opening = 0m;
            var compact = NormCompact(line);
            if (!compact.Contains("SALDOANTERIOR")) return false;

            var am = RxAmountSpaced.Matches(line);
            if (am.Count == 0) return false;

            var tok = NormalizeAmountToken(am[^1].Value);
            return decimal.TryParse(tok, NumberStyles.Number | NumberStyles.AllowLeadingSign, EsAr, out opening);
        }

        private static bool TryParseClosingBalance(string line, int yearHint, out decimal closing, out DateTime? closingDate)
        {
            closing = 0m; closingDate = null;

            var compact = NormCompact(line);
            if (!compact.Contains("SALDOAL")) return false;

            // Condensar letras separadas: "S A L D O   A L   3 0   D E   M A Y O ..."
            var lettersCondensed = CondenseSpacedLetters(RemoveDiacritics(line));
            var flat = Regex.Replace(lettersCondensed, @"\s{2,}", " ").Trim();

            var rx = new Regex(@"SALDO\s+AL\s+(?<dd>\d{1,2})\s+DE\s+(?<mes>[A-Za-zÁÉÍÓÚÜÑ]+)", RegexOptions.IgnoreCase);
            var m = rx.Match(flat);
            if (m.Success)
            {
                if (int.TryParse(m.Groups["dd"].Value, out int dd))
                {
                    string mes = m.Groups["mes"].Value.ToUpperInvariant();
                    if (MonthEs.TryGetValue(mes, out int mm))
                    {
                        if (SafeDate(dd, mm, yearHint, out var dt)) closingDate = dt;
                    }
                }
            }

            // Importe = ÚLTIMO importe en la línea
            var am = RxAmountSpaced.Matches(line);
            if (am.Count == 0) return false;

            var tok = NormalizeAmountToken(am[^1].Value);
            return decimal.TryParse(tok, NumberStyles.Number | NumberStyles.AllowLeadingSign, EsAr, out closing);
        }

        // ===================== Helpers numéricos/fecha =====================
        private static bool TryParseLatAm(string s, out decimal v)
        {
            v = 0m;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign, EsAr, out v);
        }

        private static bool SafeDate(int d, int m, int y, out DateTime dt)
        {
            dt = default;
            if (m < 1 || m > 12 || d < 1 || d > 31) return false;
            try { dt = new DateTime(y, m, d); return true; } catch { return false; }
        }

        private static int YearHint() => DateTime.Today.Year;

        private static bool PassBalanceLimit(string currency, decimal balance)
        {
            var abs = Math.Abs(balance);
            if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
                return abs <= 10_000_000m;
            return abs <= 1_000_000_000_000m;
        }

        private static Match? FindFirstDdMmAfter(string s, int startIndex)
        {
            var ms = RxDdMmAny.Matches(s);
            foreach (Match m in ms)
            {
                if (m.Index > startIndex) return m;
            }
            return null;
        }

        private static List<TxRaw> ParseTransactionsByHardRules(IEnumerable<string> rawLines, int yearHint, string currency)
        {
            var result = new List<TxRaw>();

            foreach (var raw in rawLines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (IsDecor(raw)) continue;

                var amounts = RxAmountSpaced.Matches(raw);
                if (amounts.Count != 2) continue;

                int idxMov = amounts[0].Index;
                int idxBal = amounts[1].Index;
                if (!(idxMov >= 0 && idxBal > idxMov)) continue;

                var dateMatch = FindFirstDdMmAfter(raw, idxBal);
                if (dateMatch == null) continue;

                int idxDate = dateMatch.Index;

                var movTok = NormalizeAmountToken(amounts[0].Value);
                var balTok = NormalizeAmountToken(amounts[1].Value);

                var dayTok = NormalizeDdMm(dateMatch.Groups["dd"].Value);
                var monTok = NormalizeDdMm(dateMatch.Groups["mm"].Value);

                if (!TryParseLatAm(movTok, out var mov)) continue;
                if (!TryParseLatAm(balTok, out var bal)) continue;

                var signedMov = movTok.TrimStart().StartsWith("-") ? -Math.Abs(mov) : Math.Abs(mov);

                if (!PassBalanceLimit(currency, bal)) continue;

                if (!int.TryParse(dayTok, out var d)) continue;
                if (!int.TryParse(monTok, out var mth)) continue;
                int y = yearHint;
                if (!SafeDate(d, mth, y, out var date)) continue;

                string tail = raw[(idxDate + dateMatch.Length)..];
                var org = RxOriginAfterDate.Match(tail);
                if (org.Success) tail = tail[org.Length..];
                var desc = CondenseSpacedLetters(tail);
                desc = CompactDigits(desc);

                result.Add(new TxRaw(raw, signedMov, bal, date, desc));
            }

            return result;
        }

        private static bool Reconciles(decimal prev, decimal amount, decimal balance)
        {
            var calc = prev + amount;
            return Math.Abs(calc - balance) <= 0.01m;
        }

        // ===================== API principal =====================
        public ParseResult Parse(string raw)
        {
            var warnings = new List<string>();
            var accountsMap = new Dictionary<string, AccountStatement>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(raw))
            {
                warnings.Add("Entrada vacía");
                return new ParseResult
                {
                    Statement = new BankStatement { Bank = "BBVA", Accounts = new List<AccountStatement>() },
                    Warnings = warnings
                };
            }

            // 1) Limpieza fuerte + split @@@
            string cleaned = StripControls(raw);
            var parts = cleaned.Split(new[] { "@@@" }, StringSplitOptions.None)
                               .Select(x => (x ?? string.Empty))
                               .ToList();

            if (parts.Count == 0)
            {
                warnings.Add("Texto vacío tras normalización");
                return new ParseResult
                {
                    Statement = new BankStatement { Bank = "BBVA", Accounts = new List<AccountStatement>() },
                    Warnings = warnings
                };
            }

            // 2) Detectar TODAS las cuentas a lo largo del documento
            for (int i = 0; i < parts.Count; i++)
            {
                var c = TryParseAccountLine(parts[i]);
                if (c != null)
                {
                    var key = $"{c.Account}|{c.Currency}";
                    if (!accountsMap.ContainsKey(key))
                    {
                        accountsMap[key] = new AccountStatement
                        {
                            AccountNumber = c.Account,
                            Currency = c.Currency,
                            Transactions = new List<Transaction>()
                        };
                    }
                }
            }

            // 3) Recorrer documento buscando TÍTULO o CABECERA para abrir bloques
            CuentaCtx? lastCtx = null;
            DateTime? globalEndByClosingLine = null;

            for (int i = 0; i < parts.Count; i++)
            {
                var ln = parts[i]?.Trim() ?? string.Empty;

                bool isTitle = IsMovTitle(ln);
                bool isHeader = ContainsHeaderMovs(ln);
                if (!isTitle && !isHeader) continue;

                // ==== Resolver índice de la cabecera de columnas y la cuenta (ctx) ====
                int headerIdx = -1;
                int usedAccountIdx = -1;
                CuentaCtx? ctx = null;

                if (isTitle)
                {
                    // Buscar cuenta hacia adelante (hasta 12 líneas desde el título)
                    for (int fwd = 1; fwd <= 12 && i + fwd < parts.Count; fwd++)
                    {
                        var maybe = TryParseAccountLine(parts[i + fwd]);
                        if (maybe != null) { ctx = maybe; usedAccountIdx = i + fwd; break; }
                        if (parts[i + fwd].StartsWith("<<PAGE:", StringComparison.Ordinal)) break;
                    }
                    // Buscar cabecera de columnas DESPUÉS del título (hasta 20 líneas)
                    for (int fwd = 1; fwd <= 20 && i + fwd < parts.Count; fwd++)
                    {
                        if (ContainsHeaderMovs(parts[i + fwd])) { headerIdx = i + fwd; break; }
                        if (parts[i + fwd].StartsWith("<<PAGE:", StringComparison.Ordinal)) break;
                    }
                    // Si no encontramos cuenta adelante, usar la última conocida o buscar hacia atrás
                    if (ctx == null)
                    {
                        if (lastCtx != null) ctx = lastCtx;
                        else
                        {
                            for (int back = 1; back <= 200 && i - back >= 0; back++)
                            {
                                var maybe = TryParseAccountLine(parts[i - back]);
                                if (maybe != null) { ctx = maybe; break; }
                                if (ContainsHeaderMovs(parts[i - back])) break;
                            }
                        }
                    }
                }
                else
                {
                    // isHeader = true (línea actual ya es cabecera)
                    headerIdx = i;

                    // Primero buscar cuenta hacia ATRÁS (lo usual en BBVA)
                    for (int back = 1; back <= 12 && i - back >= 0; back++)
                    {
                        var maybe = TryParseAccountLine(parts[i - back]);
                        if (maybe != null) { ctx = maybe; usedAccountIdx = i - back; break; }
                        if (IsMovTitle(parts[i - back])) break;
                    }
                    // Si no, buscar hacia ADELANTE (algunos PDFs raros)
                    if (ctx == null)
                    {
                        for (int fwd = 1; fwd <= 12 && i + fwd < parts.Count; fwd++)
                        {
                            var maybe = TryParseAccountLine(parts[i + fwd]);
                            if (maybe != null) { ctx = maybe; usedAccountIdx = i + fwd; break; }
                            if (IsMovTitle(parts[i + fwd]) || ContainsHeaderMovs(parts[i + fwd])) break;
                        }
                    }
                    // Último recurso: lastCtx
                    if (ctx == null && lastCtx != null) ctx = lastCtx;
                }

                if (ctx == null || headerIdx < 0) continue; // sin contexto o sin cabecera, no parseamos

                lastCtx = ctx;
                var accKey = $"{ctx.Account}|{ctx.Currency}";
                if (!accountsMap.TryGetValue(accKey, out var accObj))
                {
                    accObj = new AccountStatement
                    {
                        AccountNumber = ctx.Account,
                        Currency = ctx.Currency,
                        Transactions = new List<Transaction>()
                    };
                    accountsMap[accKey] = accObj;
                }

                // ==== Capturar bloque desde la línea siguiente a la CABECERA ====
                var block = new List<string>();
                bool skippedCtxAccountOnce = false;
                for (int j = headerIdx + 1; j < parts.Count; j++)
                {
                    var l2 = parts[j];

                    // Fin de bloque: nuevo título/cabecera o cambio de página
                    if (IsMovTitle(l2) || ContainsHeaderMovs(l2) || l2.StartsWith("<<PAGE:", StringComparison.Ordinal))
                        break;

                    // Línea de cuenta:
                    var accLine = TryParseAccountLine(l2);
                    if (accLine != null)
                    {
                        // Si es la misma línea de cuenta que usamos como contexto (o la primera aparición), la saltamos UNA vez.
                        if (!skippedCtxAccountOnce && usedAccountIdx == j)
                        {
                            skippedCtxAccountOnce = true;
                            continue;
                        }
                        // Si aparece una nueva cuenta distinta o una cuenta repetida más adelante, es nuevo bloque.
                        break;
                    }

                    if (IsDecor(l2)) continue;
                    block.Add(l2);
                }
                if (block.Count == 0) continue;

                // ==== SALDO ANTERIOR: buscar en primeras ~10 líneas del bloque ====
                decimal? openingInThisBlock = null;
                int removeIdx = -1;
                for (int k = 0; k < Math.Min(10, block.Count); k++)
                {
                    if (TryParseOpeningBalance(block[k], out var openBal))
                    {
                        openingInThisBlock = openBal;
                        if (accObj.OpeningBalance == null) accObj.OpeningBalance = openBal;
                        removeIdx = k;
                        break;
                    }
                }
                if (removeIdx >= 0) block.RemoveAt(removeIdx);

                // ==== SALDO AL ...: buscar desde el FINAL de TODO el bloque ====
                int closingIdx = -1;
                decimal closeBalFound = 0m;
                DateTime? closeDateFound = null;
                for (int idx = block.Count - 1; idx >= 0; idx--)
                {
                    if (TryParseClosingBalance(block[idx], YearHint(), out var closeBal, out var closeDate))
                    {
                        closingIdx = idx;
                        closeBalFound = closeBal;
                        closeDateFound = closeDate;
                        break;
                    }
                }
                if (closingIdx >= 0)
                {
                    accObj.ClosingBalance = closeBalFound;
                    if (closeDateFound.HasValue)
                    {
                        if (globalEndByClosingLine == null || closeDateFound.Value > globalEndByClosingLine.Value)
                            globalEndByClosingLine = closeDateFound;
                    }
                    block.RemoveAt(closingIdx);
                }

                // ==== Parsear transacciones ====
                int yearHint = YearHint();
                var raws = ParseTransactionsByHardRules(block, yearHint, ctx.Currency);

                // Reconciliación contable para IsSuspicious
                decimal? prevBalance = null;
                if (openingInThisBlock.HasValue) prevBalance = openingInThisBlock.Value;
                else if (accObj.Transactions.Count > 0) prevBalance = accObj.Transactions[^1].Balance;
                else if (accObj.OpeningBalance.HasValue) prevBalance = accObj.OpeningBalance.Value;

                foreach (var tx in raws)
                {
                    bool isSusp = false;
                    if (prevBalance.HasValue)
                    {
                        isSusp = !Reconciles(prevBalance.Value, tx.Amount, tx.Balance);
                        prevBalance = tx.Balance;
                    }
                    else
                    {
                        prevBalance = tx.Balance;
                    }

                    accObj.Transactions.Add(new Transaction
                    {
                        Date = tx.Date,
                        Description = tx.Description,
                        OriginalDescription = tx.Line,
                        Amount = tx.Amount,
                        Type = tx.Amount < 0 ? "debit" : "credit",
                        Balance = tx.Balance,
                        Category = null,
                        Subcategory = null,
                        CategorySource = null,
                        CategoryRuleId = null,
                        IsSuspicious = isSusp,
                        SuggestedAmount = null
                    });
                }
            }

            var accounts = accountsMap.Values.ToList();

            // 4) Periodo min/max + Fallback de ClosingBalance
            DateTime? pStart = null, pEnd = null;
            foreach (var a in accounts)
            {
                if (a.Transactions != null && a.Transactions.Count > 0)
                {
                    var min = a.Transactions.Min(t => t.Date);
                    var max = a.Transactions.Max(t => t.Date);
                    if (pStart == null || min < pStart) pStart = min;
                    if (pEnd == null || max > pEnd) pEnd = max;

                    if (a.ClosingBalance == null)
                        a.ClosingBalance = a.Transactions[^1].Balance;
                }
            }
            if (globalEndByClosingLine.HasValue)
            {
                if (pEnd == null || globalEndByClosingLine.Value > pEnd.Value)
                    pEnd = globalEndByClosingLine.Value;
            }

            // 5) Warnings
            var txCount = accounts.Sum(a => a.Transactions?.Count ?? 0);
            if (accounts.Count == 0) warnings.Add("No se detectaron cuentas.");
            if (txCount == 0) warnings.Add("No se detectaron movimientos.");
            if (pStart == null || pEnd == null) warnings.Add("No se pudo inferir el periodo (min/max).");

            return new ParseResult
            {
                Statement = new BankStatement
                {
                    Bank = "BBVA",
                    PeriodStart = pStart,
                    PeriodEnd = pEnd,
                    Accounts = accounts
                },
                Warnings = warnings
            };
        }

        // Overload con progreso (compat)
        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress)
        {
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("start", 0, 3));
            var r = Parse(text);
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("parsed", 2, 3));
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("done", 3, 3));
            return r;
        }
    }
}