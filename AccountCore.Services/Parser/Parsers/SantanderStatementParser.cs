using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using AccountCore.DAL.Parser.Models;           // BankStatement, AccountStatement, Transaction
using AccountCore.Services.Parser.Interfaces;  // IBankStatementParser

namespace AccountCore.Services.Parser.Parsers
{
    public class SantanderStatementParser : IBankStatementParser
    {
        private static readonly CultureInfo Ar = new CultureInfo("es-AR");

        private static readonly Regex RxPeriodDesde = new Regex(@"Desde:\s*([0-3]?\d/[01]?\d/\d{2})", RegexOptions.Compiled);
        private static readonly Regex RxPeriodHasta = new Regex(@"Hasta:\s*([0-3]?\d/[01]?\d/\d{2})", RegexOptions.Compiled);

        private static readonly Regex RxCuenta = new Regex(
            @"Cuenta\s+Corriente\s+N[º°]\s*([0-9]{3}-[0-9]{6}/[0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RxCuentaUsd = new Regex(
            @"Cuenta\s+Corriente\s+especial\s*U\$S\s+N[º°]\s*([0-9]{3}-[0-9]{6}/[0-9])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // === PATRÓN DE DINERO (acepta signo antes o después del símbolo) ===
        private const string Money = @"(?:(?:[-+]\s*)?(?:U\$S|\$)?|(?:U\$S|\$)\s*[-+]?)\s*\d{1,3}(?:\.\d{3})*,\d{2}";

        // Acepta signo + o - y el signo puede ir antes o después del símbolo de moneda
        private static readonly Regex RxSaldoInicial = new Regex(
            @"Saldo\s+Inicial\s+(?:(?<sign>[+\-])\s*)?(?:U\$S|\$)?\s*(?<num>\d{1,3}(?:\.\d{3})*,\d{2})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RxSaldoTotalPesos = new Regex(
            @"^\s*Saldo\s+total\s+\$?\s*([-]?\$?\s*[0-9\.\s]+,[0-9]{2})\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RxSaldoTotalMoneda = new Regex(
            @"^\s*Saldo\s+total\s+(?<cur>U\$S|\$)\s*(?<amt>[-+]?\s*[0-9\.\s]+,[0-9]{2})\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RxFechaLinea = new Regex(@"^([0-3]\d/[01]\d/\d{2})\b", RegexOptions.Compiled);

        private static readonly Regex RxImportesEnLinea = new Regex(
            @"((" + Money + @"))",
            RegexOptions.Compiled);

        private const string MarcaMovimientosPesos = "Movimientos en pesos";
        private const string MarcaMovimientosDolares = "Movimientos en dólares";
        private const string MarcaDetalleImpositivo = "Detalle impositivo";

        private static readonly Regex DateAnchor = new(
            @"(?m)(?<=^|\s)(?<date>\d{2}[\/-]\d{2}[\/-](?:\d{2}|\d{4}))(?!\d)",
            RegexOptions.Compiled);

        private static string NormalizeDatesToYYYY(string text)
        {
            return Regex.Replace(text,
                @"\b(?<d>\d{2})[\/-](?<m>\d{2})[\/-](?<y>\d{2})\b",
                m =>
                {
                    var yy = int.Parse(m.Groups["y"].Value);
                    var yyyy = yy >= 70 ? 1900 + yy : 2000 + yy;
                    return $"{m.Groups["d"].Value}/{m.Groups["m"].Value}/{yyyy}";
                });
        }

        private const decimal AmountTolerance = 0.05m;

        private static string ResolveTypeFromBalances(decimal prevBalance, decimal currBalance)
            => (currBalance - prevBalance) >= 0m ? "credit" : "debit";

        private static void ValidatePrintedVsDelta(Transaction t, decimal printedAmountAbs)
        {
            var deltaAbs = Math.Abs(t.Amount);
            if (Math.Abs(deltaAbs - printedAmountAbs) > AmountTolerance)
            {
                t.IsSuspicious = true;
                t.SuggestedAmount = t.Amount; // delta que cierra
            }
            else
            {
                t.IsSuspicious = false;
                t.SuggestedAmount = null;
            }
        }

        public ParseResult Parse(string text)
        {
            var warnings = new List<string>();
            void Log(string w) => warnings.Add(w);

            var lines = (text ?? string.Empty)
                .Replace("\r", "")
                .Split('\n')
                .Select(l => l.Replace("@@@", " ").Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var statement = new BankStatement
            {
                Bank = "Banco Santander",
                PeriodStart = TryExtractDate(lines, RxPeriodDesde, Log),
                PeriodEnd = TryExtractDate(lines, RxPeriodHasta, Log),
                Accounts = new List<AccountStatement>()
            };

            string? accountNumber = lines
                .Select(l => { var m = RxCuenta.Match(l); return m.Success ? m.Groups[1].Value.Trim() : null; })
                .FirstOrDefault(v => !string.IsNullOrEmpty(v));

            if (accountNumber == null) Log("[santander] cuenta (pesos) no encontrada");

            var acc = new AccountStatement
            {
                AccountNumber = accountNumber ?? "",
                Currency = "ARS",
                Transactions = new List<Transaction>()
            };

            int idxMovPesos = IndexOfLineContaining(lines, MarcaMovimientosPesos);
            if (idxMovPesos < 0)
            {
                Log("[santander] sección 'Movimientos en pesos' no encontrada");
                statement.Accounts.Add(acc);
                return new ParseResult { Statement = statement, Warnings = warnings };
            }

            int idxFin = lines.Count;
            var corte1 = IndexOfLineContaining(lines, MarcaMovimientosDolares, start: idxMovPesos + 1);
            var corte2 = IndexOfLineContaining(lines, MarcaDetalleImpositivo, start: idxMovPesos + 1);
            if (corte1 >= 0) idxFin = Math.Min(idxFin, corte1);
            if (corte2 >= 0) idxFin = Math.Min(idxFin, corte2);

            var pesos = lines.Skip(idxMovPesos + 1).Take(idxFin - (idxMovPesos + 1)).ToList();

            decimal? opening = null;
            for (int i = 0; i < pesos.Count; i++)
            {
                var mi = RxSaldoInicial.Match(pesos[i]);
                if (mi.Success)
                {
                    var raw = mi.Groups["num"].Value;
                    var sign = mi.Groups["sign"].Success ? mi.Groups["sign"].Value : "";
                    opening = ParseMoney($"{sign}{raw}", Log);
                    break;
                }
            }
            acc.OpeningBalance = opening;

            decimal? closing = null;
            for (int i = pesos.Count - 1; i >= 0; i--)
            {
                var mf = RxSaldoTotalPesos.Match(pesos[i]);
                if (mf.Success) { closing = ParseMoney(mf.Groups[1].Value, Log); break; }
            }
            acc.ClosingBalance = closing;

            // === Transacciones ===
            List<Transaction> txs = ParseMovimientosPesosSequential(pesos, opening, Log);
            if (txs.Count == 0) txs = ParseMovimientosPesosByDateBlocks(pesos, opening, Log);
            if (txs.Count == 0 && opening.HasValue)
            {
                Log("[santander] fallback balance-driven");
                txs = ParseMovimientosPesosBalanceDriven(pesos, opening.Value, Log);
            }

            txs = OrderAndRecomputeByDelta(txs, opening, Log);
            acc.Transactions.AddRange(txs);

            // === Validación de cierre (ARS) ===
            var lastBalArs = acc.Transactions.LastOrDefault()?.Balance;
            if (acc.ClosingBalance.HasValue && lastBalArs.HasValue)
            {
                if (Math.Abs(acc.ClosingBalance.Value - lastBalArs.Value) > AmountTolerance)
                {
                    warnings.Add($"[balance.mismatch.ARS] closing printed={acc.ClosingBalance.Value:n2} computed={lastBalArs.Value:n2}");
                    if (acc.Transactions.Count > 0)
                    {
                        var lastTx = acc.Transactions[^1];
                        lastTx.IsSuspicious = true;
                        lastTx.SuggestedAmount = lastTx.Amount;
                    }
                }
            }

            statement.Accounts.Add(acc);

            // ===== Cuenta USD (posterior) =====
            if (corte1 >= 0)
            {
                int usdStart = corte1 + 1;
                int usdEnd = lines.Count;
                var usdCorte = IndexOfLineContaining(lines, MarcaDetalleImpositivo, start: usdStart);
                if (usdCorte >= 0) usdEnd = Math.Min(usdEnd, usdCorte);

                var usdBlock = lines.Skip(usdStart).Take(usdEnd - usdStart).ToList();

                string? accUsd = usdBlock.Select(l => { var m = RxCuentaUsd.Match(l); return m.Success ? m.Groups[1].Value.Trim() : null; })
                                         .FirstOrDefault(v => !string.IsNullOrEmpty(v))
                                ?? usdBlock.Select(l => { var m = RxCuenta.Match(l); return m.Success ? m.Groups[1].Value.Trim() : null; })
                                           .FirstOrDefault(v => !string.IsNullOrEmpty(v));

                var acc2 = new AccountStatement
                {
                    AccountNumber = accUsd ?? "",
                    Currency = "USD",
                    Transactions = new List<Transaction>()
                };

                decimal? openingUsd = null;
                for (int i = 0; i < usdBlock.Count; i++)
                {
                    var mi = RxSaldoInicial.Match(usdBlock[i]);
                    if (mi.Success)
                    {
                        var raw = mi.Groups["num"].Value;
                        var sign = mi.Groups["sign"].Success ? mi.Groups["sign"].Value : "";
                        openingUsd = ParseMoney($"{sign}{raw}", Log);
                        break;
                    }
                }
                acc2.OpeningBalance = openingUsd;

                decimal? closingUsd = null;
                for (int i = usdBlock.Count - 1; i >= 0; i--)
                {
                    var mcl = RxSaldoTotalMoneda.Match(usdBlock[i]);
                    if (mcl.Success && mcl.Groups["cur"].Value.StartsWith("U$S", StringComparison.OrdinalIgnoreCase))
                    {
                        closingUsd = ParseMoney(mcl.Groups["amt"].Value, Log);
                        break;
                    }
                }
                acc2.ClosingBalance = closingUsd;

                bool noUsdMovs = usdBlock.Any(l => l.IndexOf("No tenés movimientos", StringComparison.OrdinalIgnoreCase) >= 0);

                List<Transaction> txsUsd = new();
                if (!noUsdMovs)
                {
                    txsUsd = ParseMovimientosPesosSequential(usdBlock, openingUsd, Log);
                    if (txsUsd.Count == 0) txsUsd = ParseMovimientosPesosByDateBlocks(usdBlock, openingUsd, Log);
                    if (txsUsd.Count == 0 && openingUsd.HasValue)
                        txsUsd = ParseMovimientosPesosBalanceDriven(usdBlock, openingUsd.Value, Log);

                    txsUsd = OrderAndRecomputeByDelta(txsUsd, openingUsd, Log);
                }

                acc2.Transactions.AddRange(txsUsd);

                // Validación de cierre (USD)
                var lastBalUsd = acc2.Transactions.LastOrDefault()?.Balance;
                if (acc2.ClosingBalance.HasValue && lastBalUsd.HasValue)
                {
                    if (Math.Abs(acc2.ClosingBalance.Value - lastBalUsd.Value) > AmountTolerance)
                    {
                        warnings.Add($"[balance.mismatch.USD] closing printed={acc2.ClosingBalance.Value:n2} computed={lastBalUsd.Value:n2}");
                        if (acc2.Transactions.Count > 0)
                        {
                            var lastTx = acc2.Transactions[^1];
                            lastTx.IsSuspicious = true;
                            lastTx.SuggestedAmount = lastTx.Amount;
                        }
                    }
                }

                statement.Accounts.Add(acc2);
            }

            return new ParseResult { Statement = statement, Warnings = warnings };
        }

        // === Usamos Money para tomar ambos órdenes del signo/moneda ===
        private static readonly Regex RxTwoAmts = new(@"(" + Money + @")", RegexOptions.Compiled);

        private static (decimal? printedAmtAbs, decimal? printedBal) TryGetPrintedFromOriginal(string original, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(original)) return (null, null);
            var m = RxTwoAmts.Matches(original);
            if (m.Count == 0) return (null, null);

            decimal? bal = ParseMoney(m[m.Count - 1].Value, _ => { });
            decimal? amt = null;
            if (m.Count >= 2) amt = Math.Abs(ParseMoney(m[m.Count - 2].Value, _ => { }) ?? 0m);
            return (amt, bal);
        }

        // *** Cambiado: NO inferimos prev desde el impreso si opening == null ***
        private static List<Transaction> OrderAndRecomputeByDelta(List<Transaction> txs, decimal? openingBalance, Action<string>? log = null)
        {
            if (txs == null || txs.Count == 0) return txs ?? new List<Transaction>();

            var ordered = txs.Select((t, idx) => new { t, idx })
                             .OrderBy(x => x.t.Date)
                             .ThenBy(x => x.idx)
                             .Select(x => x.t)
                             .ToList();

            decimal? prev = openingBalance;

            for (int i = 0; i < ordered.Count; i++)
            {
                var t = ordered[i];

                if (prev.HasValue)
                {
                    var delta = Math.Round(t.Balance - prev.Value, 2);
                    t.Amount = delta;
                    t.Type = delta >= 0 ? "credit" : "debit";

                    var (printedAmtAbs, _) = TryGetPrintedFromOriginal(t.OriginalDescription ?? "");
                    if (printedAmtAbs.HasValue)
                    {
                        var printedSign = t.Type == "credit" ? +1m : -1m;
                        var signedPrinted = printedSign * printedAmtAbs.Value;

                        var printedConsistente = Math.Abs(prev.Value + signedPrinted - t.Balance) <= AmountTolerance;

                        if (printedConsistente)
                        {
                            var diffMag = Math.Abs(Math.Abs(delta) - printedAmtAbs.Value);
                            t.IsSuspicious = diffMag > AmountTolerance;
                            t.SuggestedAmount = t.IsSuspicious ? delta : (decimal?)null;
                        }
                        else
                        {
                            t.IsSuspicious = false;
                            t.SuggestedAmount = null;
                        }
                    }
                    else
                    {
                        t.IsSuspicious = false;
                        t.SuggestedAmount = null;
                    }
                }
                else
                {
                    t.IsSuspicious = true; // primera sin opening
                }

                prev = t.Balance;
            }

            return ordered;
        }

        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress)
        {
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("start", 0, 3));
            var result = Parse(text);
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("parsed", 2, 3));
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("done", 3, 3));
            return result;
        }

        // === REGEX de filas (2 y 3 columnas) usando Money ===
        private static readonly Regex Op = new(
            @"(?s)(?<desc>.+?)\s+(?<amt>" + Money + @")\s{1,10}(?<bal>" + Money + @")",
            RegexOptions.Compiled);

        private static readonly Regex Op3Cols = new(
            @"^(?<desc>.+?)\s+" +
            @"(?:(?<credit>" + Money + @"))?\s+" +
            @"(?:(?<debit>" + Money + @"))?\s+" +
            @"(?<bal>" + Money + @")\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline
        );

        private static decimal ParseArs(string s)
        {
            var clean = s.Replace("U$S", "").Replace("$", "").Replace("\u00A0", "").Replace(" ", "");
            bool neg = clean.StartsWith("-");
            if (neg || clean.StartsWith("+")) clean = clean.Substring(1);
            clean = clean.Replace(".", "").Replace(",", ".");
            if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"Monto inválido: {s}");
            return neg ? -v : v;
        }

        private static int IndexOfLineContaining(List<string> lines, string marker, int start = 0)
        {
            for (int i = Math.Max(0, start); i < lines.Count; i++)
                if (lines[i].IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return i;
            return -1;
        }

        private static DateTime? TryExtractDate(List<string> lines, Regex rx, Action<string> log)
        {
            foreach (var l in lines)
            {
                var m = rx.Match(l);
                if (m.Success)
                {
                    if (DateTime.TryParseExact(m.Groups[1].Value, "dd/MM/yy", Ar, DateTimeStyles.None, out var d)) return d;
                    if (DateTime.TryParseExact(m.Groups[1].Value, "dd/MM/yyyy", Ar, DateTimeStyles.None, out d)) return d;
                }
            }
            return null;
        }

        private static decimal? ParseMoney(string raw, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var s = raw.Trim();
            s = s.Replace("U$S", "").Replace("$", "").Replace("\u00A0", "").Replace(" ", "");

            bool isNeg = s.StartsWith("-");
            if (isNeg || s.StartsWith("+")) s = s.Substring(1);

            s = s.Replace(".", "").Replace(",", ".");
            if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                log($"[money.parse.fail] raw='{raw}' norm='{s}'");
                return null;
            }
            return isNeg ? -v : v;
        }

        private static readonly string[] DebitHints = new[]
        {
            "debito", "débito", "transferencia realizada", "transferencia pagos a terceros",
            "pago haberes", "pago de haberes", "pago de servicios", "pago tarjeta",
            "comision", "comisión", "impuesto", "retito", "retiro", "multa"
        };

        private static readonly string[] CreditHints = new[]
        {
            "credito", "crédito", "transferencia recibida", "transf recibida",
            "deposito", "depósito", "cobro", "devol", "devolución", "valores", "echeq", "clearing"
        };

        private static bool LooksLikeDebit(string desc)
        {
            var d = desc.ToLowerInvariant();
            return DebitHints.Any(h => d.Contains(h)) && !CreditHints.Any(h => d.Contains(h));
        }

        private IEnumerable<Transaction> ParseDayBlock(string dateStr, string dayText, ref decimal? runningBalance)
        {
            var date = DateTime.ParseExact(dateStr.Replace("-", "/"), "dd/MM/yyyy", CultureInfo.InvariantCulture);
            int start = 0;
            var list = new List<Transaction>();

            while (true)
            {
                var m = Op.Match(dayText, start);
                if (!m.Success) break;

                string desc = Regex.Replace(m.Groups["desc"].Value, @"\s+", " ").Trim();
                string amtStr = m.Groups["amt"].Value;
                string balStr = m.Groups["bal"].Value;

                decimal nextBal = ParseArs(balStr);
                decimal printedAmtAbs = Math.Abs(ParseArs(amtStr));

                decimal signedAmt;
                string type;

                if (runningBalance.HasValue)
                {
                    signedAmt = nextBal - runningBalance.Value;
                    type = ResolveTypeFromBalances(runningBalance.Value, nextBal);
                }
                else
                {
                    signedAmt = LooksLikeDebit(desc) ? -printedAmtAbs : printedAmtAbs;
                    type = signedAmt >= 0 ? "credit" : "debit";
                }

                var t = new Transaction
                {
                    Date = date,
                    // FIX: sin "$ " duplicado
                    OriginalDescription = $"{desc}  {amtStr} {balStr}".Trim(),
                    Description = desc,
                    Amount = Math.Round(signedAmt, 2),
                    Type = type,
                    Balance = nextBal
                };

                if (runningBalance.HasValue) ValidatePrintedVsDelta(t, printedAmtAbs);

                list.Add(t);
                runningBalance = nextBal;
                start = m.Index + m.Length;
            }

            return list;
        }

        private static string StripPageNoise(string text)
        {
            text = Regex.Replace(text, @"<<PAGE:\s*\d+\s*>>>", " ");
            text = Regex.Replace(text, @"Cuenta Corriente Nº.*?Saldo en cuenta", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"\s{2,}", " ");
            return text.Trim();
        }

        private List<Transaction> ParseMovimientosPesosByDateBlocks(List<string> lines, decimal? opening, Action<string> log)
        {
            var trimmed = new List<string>();
            foreach (var l in lines)
            {
                if (RxSaldoTotalPesos.IsMatch(l) || RxSaldoTotalMoneda.IsMatch(l)) break;
                trimmed.Add(l);
            }

            var blob = NormalizeDatesToYYYY(string.Join("\n", trimmed));
            blob = StripPageNoise(blob);

            var matches = DateAnchor.Matches(blob);
            if (matches.Count == 0)
            {
                log("[santander] no se hallaron anclas de fecha (yyyy) en movimientos");
                return new List<Transaction>();
            }

            var txs = new List<Transaction>();
            decimal? lastDayClosing = opening;

            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                var dateStr = m.Groups["date"].Value;

                int start = m.Index + m.Length;
                int end = (i + 1 < matches.Count) ? matches[i + 1].Index : blob.Length;

                var dayText = blob.Substring(start, end - start).Trim();
                if (string.IsNullOrWhiteSpace(dayText)) continue;

                decimal? dayRunning = null;

                if (i == 0 && opening.HasValue)
                {
                    dayRunning = opening.Value;
                }
                else
                {
                    var first = Op.Match(dayText);
                    if (!dayRunning.HasValue && first.Success)
                    {
                        var printedAmt = Math.Abs(ParseArs(first.Groups["amt"].Value));
                        var firstBal = ParseArs(first.Groups["bal"].Value);
                        var inferredPrev = firstBal - printedAmt;

                        if (lastDayClosing.HasValue && Math.Abs(lastDayClosing.Value - inferredPrev) < 0.01m)
                            dayRunning = lastDayClosing.Value;
                    }
                }

                try
                {
                    var dayTxs = ParseDayBlock(dateStr, dayText, ref dayRunning);
                    txs.AddRange(dayTxs);
                    lastDayClosing = dayRunning;
                }
                catch (Exception ex)
                {
                    log($"[santander.dayblock.fail] {dateStr}: {ex.Message}");
                }
            }

            return txs;
        }

        private static List<Transaction> ParseMovimientosPesosBalanceDriven(List<string> lines, decimal opening, Action<string> log)
        {
            var txs = new List<Transaction>();
            decimal prevBalance = opening;

            int start = 0;
            for (int i = 0; i < lines.Count; i++) if (RxSaldoInicial.IsMatch(lines[i])) { start = i + 1; break; }

            Transaction? current = null;
            var descBuffer = new List<string>();

            for (int i = start; i < lines.Count; i++)
            {
                var line = lines[i];
                if (RxSaldoTotalPesos.IsMatch(line) || RxSaldoTotalMoneda.IsMatch(line)) break;

                var mFecha = RxFechaLinea.Match(line);
                if (mFecha.Success)
                {
                    current = new Transaction
                    {
                        Date = ParseDate(mFecha.Groups[1].Value),
                        OriginalDescription = "",
                        Description = "",
                        Amount = 0m,
                        Balance = 0m,
                        Type = "debit"
                    };
                    descBuffer.Clear();

                    var tail = line.Substring(mFecha.Length).Trim();
                    if (!string.IsNullOrEmpty(tail)) descBuffer.Add(tail);

                    if (TryCloseMovementWithLine(line, ref prevBalance, current, descBuffer, log))
                    {
                        txs.Add(current);
                        current = null;
                        descBuffer.Clear();
                    }
                    continue;
                }

                if (current != null)
                {
                    descBuffer.Add(line);
                    if (TryCloseMovementWithLine(line, ref prevBalance, current, descBuffer, log))
                    {
                        txs.Add(current);
                        current = null;
                        descBuffer.Clear();
                    }
                }
            }

            return txs;
        }

        private static bool TryCloseMovementWithLine(
            string line,
            ref decimal prevBalance,
            Transaction current,
            List<string> descBuffer,
            Action<string> log)
        {
            var matches = RxImportesEnLinea.Matches(line);
            if (matches.Count == 0) return false;

            var saldoRaw = matches[matches.Count - 1].Value;
            var saldo = ParseMoney(saldoRaw, log);
            if (saldo == null) return false;

            var amountCalc = saldo.Value - prevBalance;

            decimal? amountDeclarado = null;
            if (matches.Count >= 2)
            {
                var montoRaw = matches[matches.Count - 2].Value;
                amountDeclarado = ParseMoney(montoRaw, log);
            }

            current.Description = string.Join(" ", descBuffer).Trim();
            current.OriginalDescription = current.OriginalDescription ?? current.Description;
            current.Balance = saldo.Value;
            current.Amount = Math.Round(amountCalc, 2);
            current.Type = ResolveTypeFromBalances(prevBalance, saldo.Value);

            if (amountDeclarado.HasValue)
            {
                var magnitudesIguales = Math.Abs(Math.Abs(amountDeclarado.Value) - Math.Abs(current.Amount)) <= AmountTolerance;
                if (!magnitudesIguales)
                {
                    current.IsSuspicious = true;
                    current.SuggestedAmount = current.Amount; // delta
                }
                else
                {
                    current.IsSuspicious = false;
                    current.SuggestedAmount = null;
                }
            }
            else
            {
                current.IsSuspicious = false;
                current.SuggestedAmount = null;
            }

            prevBalance = saldo.Value;
            return true;
        }

        private static DateTime ParseDate(string ddmmyy)
        {
            if (DateTime.TryParseExact(ddmmyy, "dd/MM/yy", Ar, DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParseExact(ddmmyy, "dd/MM/yyyy", Ar, DateTimeStyles.None, out d)) return d;
            return DateTime.MinValue;
        }

        // ========= Parser secuencial (3 columnas) =========
        private List<Transaction> ParseMovimientosPesosSequential(List<string> lines, decimal? opening, Action<string> log)
        {
            var trimmed = new List<string>();
            foreach (var l in lines)
            {
                if (RxSaldoTotalPesos.IsMatch(l) || RxSaldoTotalMoneda.IsMatch(l)) break;
                trimmed.Add(l);
            }

            var norm = trimmed.Select(l => Regex.Replace(l, @"\s+", " ").Trim())
                              .Where(l => !string.IsNullOrWhiteSpace(l))
                              .ToList();

            var txs = new List<Transaction>();
            decimal? prev = opening;

            for (int i = 0; i < norm.Count; i++)
            {
                var line = norm[i];
                var mFecha = RxFechaLinea.Match(line);
                if (!mFecha.Success) continue;

                string dateStr = mFecha.Groups[1].Value;
                string rowText = line.Substring(mFecha.Length).Trim();

                var m = Op3Cols.Match(rowText);
                int j = i;
                while (!m.Success && j + 1 < norm.Count && !RxFechaLinea.IsMatch(norm[j + 1]))
                {
                    j++;
                    rowText += " " + norm[j];
                    m = Op3Cols.Match(rowText);
                }
                i = j;

                if (!m.Success)
                {
                    log($"[santander.seq.skip] '{rowText}'");
                    continue;
                }

                string desc = m.Groups["desc"].Value.Trim();
                string? creditStr = m.Groups["credit"].Success ? m.Groups["credit"].Value : null;
                string? debitStr = m.Groups["debit"].Success ? m.Groups["debit"].Value : null;
                string balStr = m.Groups["bal"].Value;

                decimal bal = ParseArs(balStr);

                decimal? printedAmtAbs = null;
                if (!string.IsNullOrEmpty(creditStr)) printedAmtAbs = Math.Abs(ParseArs(creditStr));
                else if (!string.IsNullOrEmpty(debitStr)) printedAmtAbs = Math.Abs(ParseArs(debitStr));

                // IMPORTANT: no seteamos prev desde el impreso si opening == null
                decimal delta;
                if (prev.HasValue)
                {
                    delta = Math.Round(bal - prev.Value, 2);
                }
                else
                {
                    if (printedAmtAbs.HasValue)
                        delta = !string.IsNullOrEmpty(creditStr) ? printedAmtAbs.Value : -printedAmtAbs.Value;
                    else
                        delta = 0m;
                }

                var t = new Transaction
                {
                    Date = ParseDate(dateStr),
                    Description = desc,
                    OriginalDescription = BuildOriginalFrom3Cols(desc, creditStr, debitStr, balStr),
                    Balance = bal,
                    Amount = delta,
                    Type = delta >= 0m ? "credit" : "debit",
                    IsSuspicious = false,
                    SuggestedAmount = null
                };

                if (prev.HasValue && printedAmtAbs.HasValue)
                {
                    var sameMag = Math.Abs(Math.Abs(delta) - printedAmtAbs.Value) <= AmountTolerance;
                    if (!sameMag)
                    {
                        t.IsSuspicious = true;
                        t.SuggestedAmount = delta; // delta que cierra
                    }
                }

                prev = bal;
                txs.Add(t);
            }

            return txs;
        }

        private static string BuildOriginalFrom3Cols(string desc, string? creditStr, string? debitStr, string balStr)
        {
            string printed = creditStr ?? debitStr ?? "";
            printed = printed?.Trim() ?? "";
            var parts = new List<string> { desc };
            if (!string.IsNullOrEmpty(printed)) parts.Add(printed);
            parts.Add(balStr);
            return string.Join("  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }
}