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
            @"RESUMEN\s+DE\s+CUENTA\s+DESDE\s+(\d{2}/\d{2}/\d{2})\s+HASTA\s+(\d{2}/\d{2}/\d{2})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxNumeroCuenta = new(
            @"NUMERO\s+DE\s+CUENTA\s+([0-9][0-9\-/]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxCBU = new(
            @"CLAVE\s+BANCARIA\s+UNICA\s+([0-9 ]{10,})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxOpeningBalance = new(
            @"Saldo\s+del\s+per[ií]odo\s+anterior\s+([0-9\.\,]+)\s*-?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxClosingBalance = new(
            @"SALDO\s+PERIODO\s+ACTUAL\s+([0-9\.\,]+)\s*-?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxResumenLineaCuenta = new(
            @"(?m)^\s*(?<acct>\d{2}-\d{8,}/\d)\s+(?<moneda>U\$S|\$)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RxDetalleMovs = new(
            @"Detalle\s+de\s+Movimientos",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Línea de transacción (fecha, desc, ref, importe, saldo)
        private static readonly Regex RxTxnLine = new(
            @"(?m)^\s*(?<date>\d{2}/\d{2}/\d{2})\s+(?<desc>.+?)\s+(?<ref>[A-Z0-9:\|\*]{1,})\s+(?<amount>\d{1,3}(?:\.\d{3})*,\d{2})\s+(?<balance>\d{1,3}(?:\.\d{3})*,\d{2}-?)\s*(?:@@@)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
            if (DateTime.TryParseExact(s, "dd/MM/yy", CultureInfo.GetCultureInfo("es-AR"),
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

        // Monto "es-AR" -> decimal
        private static decimal? ParseArAmount(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.GetCultureInfo("es-AR"), out var d))
                return d;
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

        private static IEnumerable<Transaction> BuildTransactionsFromRegion(string region, string account, List<string> warnings)
        {
            var list = new List<Transaction>();
            var lines = region.Split('\n')
                              .Select(s => s.TrimEnd())
                              .Where(s => s.Length > 0)
                              .ToList();

            int i = 0;
            decimal? prevBal = null;

            while (i < lines.Count)
            {
                var line = lines[i];

                var m = RxTxnLine.Match(line);
                if (!m.Success)
                {
                    i++;
                    continue;
                }

                // Parse de la línea principal
                var dateS = m.Groups["date"].Value;
                var desc = m.Groups["desc"].Value.Trim();
                var rref = m.Groups["ref"].Value.Trim();
                var amtS = m.Groups["amount"].Value.Trim();
                var balS = m.Groups["balance"].Value.Trim();

                var date = ParseDmy(dateS) ?? default;
                var amtAbs = ParseArAmount(amtS) ?? 0m;

                decimal? bal = ParseArAmount(balS.TrimEnd('-'));
                if (balS.EndsWith("-", StringComparison.Ordinal)) bal = -(bal ?? 0m);

                var fullDesc = string.IsNullOrWhiteSpace(rref) ? desc : $"{desc} | REF:{rref}";

                // Importe con signo (intentamos deducirlo con delta de saldos)
                decimal signedAmount = amtAbs;
                if (prevBal.HasValue && bal.HasValue)
                {
                    var delta = bal.Value - prevBal.Value;
                    if (Math.Abs(delta - amtAbs) <= 0.05m)
                        signedAmount = delta;
                    else
                        signedAmount = Math.Sign(delta) * amtAbs;
                }
                else
                {
                    var looksCredit = InferType(null, null, null, fullDesc) == TxType.Credit;
                    signedAmount = looksCredit ? +amtAbs : -amtAbs;
                }

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

            // Slices por cuenta
            var slices = new List<(string account, string? cbu, string slice)>();
            for (int i = 0; i < anchors.Count; i++)
            {
                var acc = anchors[i].Account;
                var start = anchors[i].StartIdx;
                var end = (i + 1 < anchors.Count) ? anchors[i + 1].StartIdx : normalized.Length;
                var slice = Slice(normalized, start, end);
                var cbu = FindCbu(slice);
                slices.Add((acc, cbu, slice));
            }

            if (DIAGNOSTIC)
            {
                var list = string.Join(", ", slices.Select(s => $"{s.account}{(string.IsNullOrEmpty(s.cbu) ? "" : $"(CBU:{s.cbu})")}"));
                result.Warnings.Add($"[accounts.detected] {slices.Count}: {list}");
            }

            // Construcción de cuentas
            int idx = 0;
            foreach (var (account, cbu, slice) in slices)
            {
                var currency = InferCurrency(slice, account, currencyMap);
                var region = ExtractMovementsRegion(slice);

                decimal? opening = null, closing = null;
                var m1 = RxOpeningBalance.Match(region);
                if (m1.Success) opening = ParseArAmount(m1.Groups[1].Value);

                var m2 = RxClosingBalance.Match(region);
                if (m2.Success) closing = ParseArAmount(m2.Groups[1].Value);

                var txs = BuildTransactionsFromRegion(region, account, result.Warnings).ToList();

                // === Post-procesado mínimo: marcar sospechosas + sugerir importes ===
                const decimal TOL = 0.01m;
                var esAr = CultureInfo.GetCultureInfo("es-AR");

                // Limpieza menor
                foreach (var tx in txs)
                {
                    if (!string.IsNullOrWhiteSpace(tx.Description))
                    {
                        tx.Description = tx.Description
                            .Replace("<<PAGE:5>>>", "")
                            .Replace("<<PAGE:4>>>", "")
                            .Replace("<<PAGE:3>>>", "")
                            .Replace("<<PAGE:2>>>", "")
                            .Replace("<<PAGE:1>>>", "")
                            .Trim();
                    }
                }

                // Chequeo contra saldo impreso → sugerir importe para reconciliar
                decimal running = opening ?? 0m;
                for (int iTx = 0; iTx < txs.Count; iTx++)
                {
                    var tx = txs[iTx];
                    var suggested = tx.Balance - running;

                    if (Math.Abs(suggested - tx.Amount) > TOL)
                    {
                        tx.IsSuspicious = true;
                        tx.SuggestedAmount = suggested;
                        if (DIAGNOSTIC)
                        {
                            result.Warnings.Add(
                                $"[suspicious:{account}] {tx.Date:dd/MM/yy} '{tx.Description}' " +
                                $"importe={tx.Amount.ToString("N2", esAr)} no cuadra con saldo impreso " +
                                $"→ sugerido={suggested.ToString("N2", esAr)}");
                        }
                    }

                    // avanzar usando el saldo impreso del banco como guía
                    running = tx.Balance;

                    // salto raro a 0 con importe chico
                    if (iTx > 0)
                    {
                        var prevBal = txs[iTx - 1].Balance;
                        var bal = tx.Balance;
                        if (Math.Abs(prevBal) > 1_000_000m &&
                            Math.Abs(prevBal) > 10m * Math.Abs(tx.Amount) &&
                            Math.Abs(bal) < 0.01m)
                        {
                            tx.IsSuspicious = true;
                            if (DIAGNOSTIC)
                                result.Warnings.Add($"[suspicious:{account}] {tx.Date:dd/MM/yy} '{tx.Description}' salto a $0 con importe pequeño.");
                        }
                    }
                }

                // c) Conciliación general (solo warning textual, no toca modelos)
                if (opening.HasValue && closing.HasValue)
                {
                    var sumAmounts = txs.Sum(t => t.Amount);
                    var expected = opening.Value + sumAmounts;
                    if (Math.Abs(expected - closing.Value) > TOL && DIAGNOSTIC)
                    {
                        result.Warnings.Add(
                            $"[reconcile] {account} opening={opening.Value.ToString("N2", esAr)} " +
                            $"sum={sumAmounts.ToString("N2", esAr)} expected={expected.ToString("N2", esAr)} " +
                            $"closing={closing.Value.ToString("N2", esAr)}");
                    }
                }

                if (DIAGNOSTIC)
                {
                    result.Warnings.Add($"[account.slice #{++idx}] account={account} currency~{currency} len={slice.Length}");
                    if (opening.HasValue || closing.HasValue)
                    {
                        result.Warnings.Add($"[balances.detected] {account} opening={opening?.ToString("N2", esAr)} closing={closing?.ToString("N2", esAr)}");
                    }
                }

                result.Statement.Accounts.Add(new AccountStatement
                {
                    AccountNumber = account,
                    Transactions = txs,
                    OpeningBalance = opening,
                    ClosingBalance = closing,
                    Currency = currency
                });
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
