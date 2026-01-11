using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using AccountCore.DAL.Parser.Models;           // BankStatement, AccountStatement, Transaction
using AccountCore.Services.Parser.Interfaces;  // IBankStatementParser

namespace AccountCore.Services.Parser.Parsers
{
    /// <summary>
    /// Banco Supervielle — Parser v0.6-slim
    /// - Multicuenta + período + saldos + transacciones
    /// - Marca transacciones sospechosas con Transaction.IsSuspicious = true
    /// - Cuando detecta descuadre vs saldo impreso, llena Transaction.SuggestedAmount
    /// - No usa tipos extra (flags/confidence/etc.)
    /// </summary>
    public class SupervielleStatementParser : IBankStatementParser
    {
        // ===== Config =====
        private static readonly bool DIAGNOSTIC = true;
        private static readonly bool RAW_FULL = true;
        private static readonly int RAW_CHUNK_SIZE = 1600;
        private static readonly int RAW_MAX_CHUNKS = 999;

        // Delimitadores del servicio
        private const string PAGE_TAG_FMT = "<<PAGE:{0}>>>";
        private const string LINE_DELIM = "@@@";

        // ===== Regex =====
        private static readonly Regex RxPeriodo = new(
            @"RESUMEN\s+DE\s+CUENTA\s+(?:DESDE\s+|DEL\s+)(\d{2}/\d{2}/\d{2,4})\s+(?:HASTA\s+|AL\s+)(\d{2}/\d{2}/\d{2,4})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxNumeroCuenta = new(
            @"(?:NUMERO\s+DE\s+CUENTA|Nro\.:)\s*([0-9][0-9\-/]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxCBU = new(
            @"(?:CLAVE\s+BANCARIA\s+UNICA|CBU)\s*([0-9 ]{10,})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxOpeningBalance = new(
            @"Saldo\s+del\s+per[ií]odo\s+anterior\s+(?<val>[0-9\.\,]+)\s*(?<sign>-)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxClosingBalance = new(
            @"SALDO\s+PERIODO\s+ACTUAL\s+(?<val>[0-9\.\,]+)\s*(?<sign>-)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxResumenLineaCuenta = new(
            @"(?m)^\s*(?<acct>\d{2}-\d{8,}/\d)\s+(?<moneda>U\$S|\$)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxDetalleMovs = new(
            @"Detalle\s+de\s+Movimientos",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Línea de transacción (fecha, desc, ref, importe, saldo)
        private static readonly Regex RxTxnLine = new(
            @"(?m)^\s*
            (?<date>\d{2}/\d{2}/\d{2})\s+
            (?<desc>.+?)\s+
            (?<ref>[A-Za-z0-9 .:\-/*]+)?\s+
            (?<amount>\d{1,3}(?:\.\d{3})*,\d{2}-?)\s+
            (?<balance>\d{1,3}(?:\.\d{3})*,\d{2}-?)\s*
            (?:@@@)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

        // Continuaciones útiles
        private static readonly Regex RxContOperacion = new(
            @"(?m)^\s*Operaci[oó]n\s+[A-Z0-9]+.*" + LINE_DELIM + @"?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RxContCuentasPropias = new(
            @"(?m)^\s*Cuentas\s+Propias\s*" + LINE_DELIM + @"?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex RxContCBU = new(
            @"(?m)^\s*CBU:\s*\d{5,}-\d{5,}.*" + LINE_DELIM + @"?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxDateAtStart = new(@"^\s*\d{2}/\d{2}/\d{2}\b");

        private readonly record struct Anchor(string Account, int StartIdx);

        // ===== Helpers =====
        private static IEnumerable<string> Chunk(string s, int size)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            for (int i = 0; i < s.Length; i += size)
                yield return s.Substring(i, Math.Min(size, s.Length - i));
        }

        private static void EmitRawFull(ParseResult result, string raw)
        {
            if (!RAW_FULL) return;
            var txt = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            int n = 0;
            foreach (var c in Chunk(txt, RAW_CHUNK_SIZE))
            {
                result.Warnings.Add($"[raw-full #{++n}] {c}");
                if (n >= RAW_MAX_CHUNKS) break;
            }
            result.Warnings.Add($"[raw-full] total_chunks={n}, total_chars={txt.Length}");
        }

        private static string NormalizeWhole(string t)
        {
            if (string.IsNullOrEmpty(t)) return string.Empty;
            t = t.Replace("\r\n", "\n").Replace('\r', '\n');
            t = Regex.Replace(t, @"[ \t]+", " ");
            return t;
        }

        private static DateTime? ParseDmy(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string[] formats = { "dd/MM/yy", "dd/MM/yyyy" };
            if (DateTime.TryParseExact(s.Trim(), formats, CultureInfo.GetCultureInfo("es-AR"),
                                       DateTimeStyles.None, out var d))
                return d;
            return null;
        }

        private static (DateTime? from, DateTime? to) DetectPeriod(string text)
        {
            var m = RxPeriodo.Match(text);
            if (!m.Success) return (null, null);
            return (ParseDmy(m.Groups[1].Value), ParseDmy(m.Groups[2].Value));
        }

        private static List<Anchor> FindAccountAnchors(string text)
        {
            var list = new List<Anchor>();
            foreach (Match m in RxNumeroCuenta.Matches(text))
                list.Add(new Anchor(m.Groups[1].Value.Trim(), m.Index));

            if (list.Count == 0)
            {
                // Fallback: buscar "CUENTA CORRIENTE" si no hay match de "Nro"
                int idx = text.IndexOf("CUENTA CORRIENTE", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) list.Add(new Anchor("Default", idx));
            }

            return list.OrderBy(t => t.StartIdx).ToList();
        }

        private static string Slice(string text, int start, int endExclusive)
        {
            start = Math.Max(0, Math.Min(text.Length, start));
            endExclusive = Math.Max(start, Math.Min(text.Length, endExclusive));
            return text[start..endExclusive];
        }

        private static string? FindCbu(string slice)
        {
            var m = RxCBU.Match(slice);
            if (!m.Success) return null;
            return Regex.Replace(m.Groups[1].Value, @"\s+", "");
        }

        private static Dictionary<string, string> MapCurrenciesFromSummaryTable(string fullText)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in RxResumenLineaCuenta.Matches(fullText))
            {
                var acct = m.Groups["acct"].Value.Trim();
                var mon = m.Groups["moneda"].Value.Trim();
                var curr = mon.Equals("U$S", StringComparison.OrdinalIgnoreCase) ? "USD" : "ARS";
                if (!dict.ContainsKey(acct)) dict[acct] = curr;
            }
            if (!dict.ContainsKey("Default")) dict["Default"] = "ARS";
            return dict;
        }

        private static string InferCurrency(string slice, string accountNumber, Dictionary<string, string> mapFromHeader)
        {
            if (mapFromHeader.TryGetValue(accountNumber, out var curr))
                return curr;
            if (slice.IndexOf("U$S", StringComparison.OrdinalIgnoreCase) >= 0) return "USD";
            return "ARS";
        }

        private static string ExtractMovementsRegion(string accountSlice)
        {
            var start = RxDetalleMovs.Match(accountSlice);
            if (!start.Success) return accountSlice;
            return accountSlice[start.Index..];
        }

        // Monto "es-AR" -> decimal (soporta signo al final)
        private static decimal? ParseArAmount(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string clean = s.Trim();
            bool negative = false;
            if (clean.EndsWith("-"))
            {
                negative = true;
                clean = clean.TrimEnd('-').Trim();
            }
            if (decimal.TryParse(clean, NumberStyles.Number, CultureInfo.GetCultureInfo("es-AR"), out var d))
                return negative ? -d : d;
            return null;
        }

        enum TxType { Debit, Credit }

        static TxType InferType(decimal? amount, decimal? prevBalance, decimal? newBalance, string description)
        {
            var d = (description ?? "").ToUpperInvariant();

            // 1) Si tenemos ambos saldos, usar delta.
            if (prevBalance.HasValue && newBalance.HasValue && prevBalance.Value != newBalance.Value)
            {
                var delta = newBalance.Value - prevBalance.Value;
                return delta > 0m ? TxType.Credit : TxType.Debit;
            }

            // 2) Overrides por keywords
            if (d.Contains("CRED BCA") || d.Contains("ACREDIT") || d.Contains("ACRED")
                || d.Contains("DEPÓSITO") || d.Contains("DEPOSITO") || d.Contains("TRANSFERENCIA RECIBIDA"))
                return TxType.Credit;

            if (d.Contains("IMPUESTO") || d.Contains("DÉBITO") || d.Contains("DEBITO") || d.Contains("PAGO")
                || d.Contains("TRF.") || d.Contains("EMBARGO")
                || d.Contains("COMISION") || d.Contains("COMISIÓN") || d.Contains("SELLOS"))
                return TxType.Debit;

            if (amount.HasValue)
                return amount.Value < 0m ? TxType.Debit : TxType.Credit;

            return TxType.Debit;
        }

        private static IEnumerable<Transaction> BuildTransactionsFromRegion(string region, string account, List<string> warnings, decimal? initialPrevBal = null)
        {
            var list = new List<Transaction>();
            var lines = region.Split('\n')
                              .Select(s => s.TrimEnd())
                              .Where(s => s.Length > 0)
                              .ToList();

            int i = 0;
            decimal? prevBal = initialPrevBal;

            while (i < lines.Count)
            {
                var line = lines[i];

                // Regex flexible para capturar fecha y luego buscar montos al final de la línea
                var mDate = Regex.Match(line, @"^\s*(?<date>\d{2}/\d{2}/\d{2})\s+(?<rest>.+)$");
                if (!mDate.Success)
                {
                    i++;
                    continue;
                }

                var dateS = mDate.Groups["date"].Value;
                var rest = mDate.Groups["rest"].Value.Trim();

                // Buscar montos al final de la línea. Supervielle suele tener [Importe] [Saldo] o [Saldo]
                var moneyMatches = Regex.Matches(rest, @"\d{1,3}(?:\.\d{3})*,\d{2}-?");
                if (moneyMatches.Count < 1)
                {
                    i++;
                    continue;
                }

                string amtS, balS, desc;
                if (moneyMatches.Count >= 2)
                {
                    // Tenemos al menos dos montos (Importe y Saldo)
                    var mBalance = moneyMatches[^1];
                    var mAmount = moneyMatches[^2];
                    
                    balS = mBalance.Value;
                    amtS = mAmount.Value;
                    desc = rest.Substring(0, mAmount.Index).Trim();
                }
                else
                {
                    // Solo un monto (probablemente Saldo, común en algunas líneas)
                    var mBalance = moneyMatches[0];
                    balS = mBalance.Value;
                    amtS = "0,00";
                    desc = rest.Substring(0, mBalance.Index).Trim();
                }

                var date = ParseDmy(dateS) ?? default;
                var amtValue = ParseArAmount(amtS) ?? 0m;
                var amtAbs = Math.Abs(amtValue);

                // Limpieza de descripción (quitar basura al final si quedó algo antes del monto)
                desc = Regex.Replace(desc, @"\s{2,}.*$", "").Trim();
                
                if (desc.Equals("SUBTOTAL", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                decimal? bal = ParseArAmount(balS);

                var fullDesc = desc;

                // Importe con signo (intentamos deducirlo con delta de saldos)
                decimal signedAmount = amtValue;
                if (prevBal.HasValue && bal.HasValue)
                {
                    var delta = bal.Value - prevBal.Value;
                    if (Math.Abs(delta - amtValue) <= 0.05m)
                        signedAmount = delta;
                    else
                        signedAmount = Math.Sign(delta) * amtAbs;
                }
                else
                {
                    var looksCredit = InferType(null, null, null, fullDesc) == TxType.Credit;
                    signedAmount = looksCredit ? +amtAbs : -amtAbs;
                }

                var dUpper = fullDesc.ToUpperInvariant();
                if (dUpper.StartsWith("DB.") || dUpper.StartsWith("DÉBITO") || dUpper.StartsWith("DEBITO"))
                    signedAmount = -Math.Abs(amtAbs);
                else if (dUpper.StartsWith("CR.") || dUpper.StartsWith("CRÉDITO") || dUpper.StartsWith("CREDITO") || dUpper.StartsWith("CRED BCA"))
                    signedAmount = +Math.Abs(amtAbs);


                var tx = new Transaction
                {
                    Date = date,
                    Description = fullDesc,
                    Amount = signedAmount,
                    Balance = bal ?? 0m,
                    Type = signedAmount < 0m ? "debit" : (signedAmount > 0m ? "credit" : "neutral"),
                    IsSuspicious = false,
                    SuggestedAmount = null
                };
                list.Add(tx);

                // Saldo previo para la próxima línea
                prevBal = bal;

                // Continuaciones útiles
                i++;
                while (i < lines.Count)
                {
                    var nxt = lines[i];
                    if (RxDateAtStart.IsMatch(nxt)) break;

                    bool isCont =
                        RxContOperacion.IsMatch(nxt) ||
                        RxContCuentasPropias.IsMatch(nxt) ||
                        RxContCBU.IsMatch(nxt) ||
                        nxt.StartsWith("<<PAGE:", StringComparison.OrdinalIgnoreCase) ||
                        nxt.StartsWith("Benef:", StringComparison.OrdinalIgnoreCase) ||
                        nxt.StartsWith("Ref:", StringComparison.OrdinalIgnoreCase);

                    if (!isCont) break;

                    var clean = Regex.Replace(nxt, LINE_DELIM + "$", "").Trim();
                    if (!string.IsNullOrWhiteSpace(clean) &&
                        !tx.Description.Contains(clean, StringComparison.OrdinalIgnoreCase))
                    {
                        tx.Description = $"{tx.Description} | {clean}";
                        if (DIAGNOSTIC) warnings.Add($"[tx.continuation] {account} {clean}");
                    }

                    i++;
                }
            }

            warnings.Add($"[tx.patterns] {account} candidates={list.Count}");
            return list;
        }

        public ParseResult Parse(string text)
        {
            var result = new ParseResult
            {
                Statement = new BankStatement
                {
                    Bank = "Banco Supervielle",
                    Accounts = new List<AccountStatement>()
                },
                Warnings = new List<string>()
            };

            // === RAW debug (opcional) ===
            if (DIAGNOSTIC) EmitRawFull(result, text ?? string.Empty);

            // === Normalización ===
            var normalized = NormalizeWhole(text ?? string.Empty);

            // === Período ===
            var (from, to) = DetectPeriod(normalized);
            result.Statement.PeriodStart = from;
            result.Statement.PeriodEnd = to;
            if (DIAGNOSTIC && (from.HasValue || to.HasValue))
                result.Warnings.Add($"[period.detected] from={(from?.ToString("dd/MM/yy") ?? "-")} to={(to?.ToString("dd/MM/yy") ?? "-")}");

            // === Detección de cuentas ===
            var anchors = FindAccountAnchors(normalized);
            if (anchors.Count == 0)
            {
                result.Statement.Accounts.Add(new AccountStatement
                {
                    AccountNumber = "(unknown)",
                    Transactions = new List<Transaction>(),
                    OpeningBalance = null,
                    ClosingBalance = null,
                    Currency = "ARS"
                });
                if (DIAGNOSTIC) result.Warnings.Add("[accounts.detected] none → placeholder (unknown)");
                return result;
            }

            // Mapeo cuenta→moneda desde la portada
            var currencyMap = MapCurrenciesFromSummaryTable(normalized);
            if (DIAGNOSTIC && currencyMap.Count > 0)
                result.Warnings.Add($"[currency.map] {string.Join(", ", currencyMap.Select(kv => kv.Key + "→" + kv.Value))}");

            // 1. Agrupar regiones por número de cuenta para evitar duplicados por página
            var accountGroups = new Dictionary<string, List<string>>();
            for (int i = 0; i < anchors.Count; i++)
            {
                var acc = anchors[i].Account;
                var start = anchors[i].StartIdx;
                var end = (i + 1 < anchors.Count) ? anchors[i + 1].StartIdx : normalized.Length;
                var slice = Slice(normalized, start, end);
                
                if (!accountGroups.ContainsKey(acc)) accountGroups[acc] = new List<string>();
                accountGroups[acc].Add(slice);
            }

            if (DIAGNOSTIC)
                result.Warnings.Add($"[accounts.detected] {accountGroups.Count} unique accounts");

            // 2. Procesar cada cuenta única consolidando sus páginas
            foreach (var kvp in accountGroups)
            {
                var account = kvp.Key;
                var slices = kvp.Value;
                
                var allTxs = new List<Transaction>();
                decimal? firstOpening = null;
                decimal? lastClosing = null;
                string currency = "ARS";

                foreach (var slice in slices)
                {
                    // Detectar saldos de este slice (página)
                    decimal? sliceOpening = null;
                    decimal? sliceClosing = null;
                    
                    var region = ExtractMovementsRegion(slice);

                    var m1 = RxOpeningBalance.Match(region);
                    if (m1.Success) sliceOpening = ParseArAmount(m1.Groups["val"].Value + m1.Groups["sign"].Value);

                    var m2 = RxClosingBalance.Match(region);
                    if (m2.Success) sliceClosing = ParseArAmount(m2.Groups["val"].Value + m2.Groups["sign"].Value);

                    // El opening es el de la primera página que lo tenga
                    if (!firstOpening.HasValue && sliceOpening.HasValue) firstOpening = sliceOpening;
                    
                    // El closing es el de la última página que lo tenga
                    if (sliceClosing.HasValue) lastClosing = sliceClosing;

                    if (currency == "ARS") // Solo intentar inferir si no es USD
                        currency = InferCurrency(slice, account, currencyMap);

                    // Usar el saldo de la última transacción del slice anterior como inicial para el actual
                    decimal? lastKnownBal = allTxs.LastOrDefault()?.Balance ?? firstOpening;
                    var pageTxs = BuildTransactionsFromRegion(region, account, result.Warnings, lastKnownBal);
                    
                    // Deduplicación básica: evitar re-agregar transacciones que ya existen (por fecha, monto y saldo)
                    foreach (var tx in pageTxs)
                    {
                        bool exists = allTxs.Any(t => 
                            t.Date == tx.Date && 
                            Math.Abs(t.Amount - tx.Amount) < 0.01m && 
                            Math.Abs(t.Balance - tx.Balance) < 0.01m &&
                            t.Description == tx.Description);
                        
                        if (!exists)
                            allTxs.Add(tx);
                    }
                }

                // === Post-procesado: marcar sospechosas + sugerir importes ===
                const decimal TOL = 0.01m;
                var esAr = CultureInfo.GetCultureInfo("es-AR");

                foreach (var tx in allTxs)
                {
                    if (!string.IsNullOrWhiteSpace(tx.Description))
                    {
                        tx.Description = Regex.Replace(tx.Description, @"<<PAGE:\d+>>>", "").Trim();
                    }
                }

                // Chequeo contra saldo impreso → sugerir importe para reconciliar
                decimal running = firstOpening ?? 0m;
                for (int iTx = 0; iTx < allTxs.Count; iTx++)
                {
                    var tx = allTxs[iTx];
                    var suggested = tx.Balance - running;

                    if (Math.Abs(suggested - tx.Amount) > TOL)
                    {
                        tx.IsSuspicious = true;
                        tx.SuggestedAmount = suggested;
                    }

                    // Avanzar usando el saldo impreso del banco como guía
                    running = tx.Balance;

                    // Salto raro a 0 con importe chico
                    if (iTx > 0)
                    {
                        var prevBal = allTxs[iTx - 1].Balance;
                        var bal = tx.Balance;
                        if (Math.Abs(prevBal) > 1_000_000m &&
                            Math.Abs(prevBal) > 10m * Math.Abs(tx.Amount) &&
                            Math.Abs(bal) < 0.01m)
                        {
                            tx.IsSuspicious = true;
                        }
                    }
                }

                result.Statement.Accounts.Add(new AccountStatement
                {
                    AccountNumber = account,
                    Transactions = allTxs,
                    OpeningBalance = firstOpening,
                    ClosingBalance = lastClosing,
                    Currency = currency
                });

                if (DIAGNOSTIC)
                    result.Warnings.Add($"[account.merged] account={account} txs={allTxs.Count} opening={firstOpening} closing={lastClosing}");
            }

            if (DIAGNOSTIC)
                result.Warnings.Add("[note] Supervielle v0.6-slim: período, cuentas, saldos y transacciones + sospechosas");

            return result;
        }

        // Implementación requerida por la interfaz (forward simple)
        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress)
            => Parse(text);
    }
}
