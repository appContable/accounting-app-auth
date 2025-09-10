using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;
using Microsoft.AspNetCore.Http;

namespace AccountCore.Services.Parser.Parsers
{
    /// <summary>
    /// Transacción = bloque desde una línea que COMIENZA con fecha dd/MM/yy (ML) hasta ANTES de la próxima línea que comience con fecha.
    /// Reglas duras en el bloque:
    /// - EXACTAMENTE 2 montos válidos:
    ///     * Importe: formateo estricto (miles '.' y decimales ','), débito con '-' prefijo.
    ///     * Saldo:   formateo estricto, negativo con '-' sufijo.
    /// - Todo lo demás es descripción TAL CUAL.
    /// Meta:
    /// - PeriodStart/PeriodEnd desde fechas en portada (si hay 2).
    /// - Opening/Closing desde portada con ancla '$': closing = $…- ; opening = $… (positivo) en la sección “Saldos”
    ///   o en su defecto el mayor $ positivo de la portada.
    /// </summary>
    public class GaliciaStatementParser : IBankStatementParser
    {
        // ===== Patrones estrictos =====
        // Fecha dd/MM/yy al inicio de línea (multilínea, tolera espacios alrededor de '/')
        // Fecha dd/MM/yy al inicio de línea (tolera espacios dentro de cada par de dígitos)
        private static readonly Regex RxDateLineAnchor =
            new(@"(?m)^\s*(?<d>\d(?:\s?\d)\s*/\s*\d(?:\s?\d)\s*/\s*\d(?:\s?\d))\b", RegexOptions.Compiled);

        // Núcleo de monto: 1 a 3 grupos de miles + ",dd"
        private const string MoneyCore = @"\d{1,3}(?:\.\d{3}){0,6},\d{2}";

        private static readonly Regex RxMoneyStrict = new($@"^(?:{MoneyCore})$", RegexOptions.Compiled);

        // Acepta: 1.234,56  | -1.234,56  | 1.234,56-
        // y permite espacios entre los dos decimales: ",5 6"
        private static readonly Regex RxStrictAmount = new(
            @"(?:(?<pref>-)\s*)?(?<m>\d{1,3}(?:\.\d{3})*,\s*\d\s*\d)(?<suff>\s*-)?",
            RegexOptions.Compiled);

        // ===== Regex para parsear montos y saldo (robusto con OCR) =====
        // Toma "-" como signo SOLO si está al inicio de línea o precedido por espacio.
        // Permite "-" de saldo NEGATIVO pegado al final (e.g. "83.350,58-").
        private static readonly Regex RxMoneyForParsing = new(
            @"\$?\s*(?<sign>(?:(?<=^)|(?<=\s))-)?(?<num>\d{1,3}(?:\.\d{3}){0,6},\d{2})(?<saldoNeg>-)?",
            RegexOptions.Compiled);

        // Parser de "1.234.567,89" -> 1234567.89 (invariant)
        private static decimal ParseEsDecimal(string num)
        {
            var clean = num.Replace(".", "").Replace(",", ".");
            return decimal.Parse(clean, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Normalización mínima de glitches OCR (ANTES de parsear montos)
        private static string NormalizeOcrGlitches(string t)
        {
            if (string.IsNullOrEmpty(t)) return t;

            // "-1 1.996.000,00" -> "-11.996.000,00"
            t = Regex.Replace(t, @"-(\d)\s+(?=\1(?:\.\d{3}){1,6},\d{2}\b)", "-$1");

            // "24.01 1,36" -> "24.011,36"
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d,\d{2}\b)", "");

            // "- 333,41" -> "-333,41"
            t = Regex.Replace(t, @"-\s+(?=\d)", "-");

            // "99 -" -> "99-"
            t = Regex.Replace(t, @"(?<=\d)\s+-\b", "-");

            return t;
        }

        // Devuelve SIEMPRE los DOS ÚLTIMOS montos del bloque:
        //   - amount: con signo por prefijo (si "sign" existe)
        //   - balance: negativo si encuentra sufijo "-"
        // Importante: NO llamarla con el texto ya "limpiado" de descripción.
        private static (decimal amount, decimal balance)? TryParseAmountAndBalance(string blockText)
        {
            var norm = NormalizeDashes(NormalizeOcrGlitches(blockText));
            var ms = RxMoneyForParsing.Matches(norm);
            if (ms.Count < 2) return null;

            var saldoM = ms[^1];
            var movM = ms[^2];

            decimal balance = ParseEsDecimal(saldoM.Groups["num"].Value);
            if (saldoM.Groups["saldoNeg"].Success) balance = -balance;

            decimal amount = ParseEsDecimal(movM.Groups["num"].Value);
            if (movM.Groups["sign"].Success) amount = -amount;

            // Defensa específica: no inviertas por guiones pegados a texto (p.ej. "IMP.CRE.LEY 25413-83.351,31")
            // Si no hay espacio/ini antes del "-", RxMoneyForParsing NO lo toma como signo (queda positivo).
            return (amount, balance);
        }

        // Portada (página 1)
        private static readonly Regex RxDollarAmount =
            new(@"\$\s*(?<m>\d{1,3}(?:\s?\.\s?\d{3})*,\s*\d\s*\d)(?<neg>-)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Portada (página 1): aceptar año de 2 o 4 dígitos

        private static readonly Regex RxAnyDate =
            new(@"\b(?<d>\d{2}\s*/\s*\d{2}\s*/\s*(?:\d{2}|\d{4}))\b", RegexOptions.Compiled);

        // Meta portada: N° de cuenta, CBU, CUIT
        private static readonly Regex RxAccountNumber =
            new(@"(?i)\bN[°º]\s*(?<raw>[\d\s\-]+)\b", RegexOptions.Compiled);

        private static readonly Regex RxCBU =
            new(@"(?i)\bCBU\b.*?(?<cbu>\d{22})", RegexOptions.Compiled);

        private static readonly Regex RxCUIT =
            new(@"(?i)\bCUIT\b.*?(?<cuit>\d{2}-\d{8}-\d)", RegexOptions.Compiled);


        // Monto "delimitado": no pegado a letras/dígitos a izquierda/derecha
        private static readonly Regex RxMoneyDelimited = new Regex(
            //  1) opcional '-' adelante (importe), 2) número con puntos y coma, 3) opcional '-' final (saldo)
            //  Acepta espacios entre dígitos/puntos/coma: 1 .528 .895,1 1
            @"(?<![\p{L}\p{Nd}])\s*(-?\s*(?:\d{1,3}(?:\s?\.\s?\d{3})*),\s?\d{2}-?)\s*(?![\p{L}\p{Nd}])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        // Fecha al inicio de línea (para removerla de la descripción visible)
        private static readonly Regex RxLeadingDate =
            new(@"(?m)^\s*\d{1,2}\s*/\s*\d{1,2}\s*/\s*\d{2}\s*", RegexOptions.Compiled);

        // Monto "flexible" y delimitado: acepta espacios OCR entre miles y centavos, opcional '$',
        // signo '-' prefijo (débito) o sufijo (saldo negativo), y no pegado a letras/dígitos.
        private static readonly Regex RxMoneyLooseDelimited = new(
            @"(?<![\p{L}\p{Nd}])\$?\s*-?\d{1,3}(?:\.\s*\d{3}){0,4},\s*\d\s*\d-?(?![\p{L}\p{Nd}])",
            RegexOptions.Compiled);

        // Línea "Mes Año" tipo "Marzo 2023" (opcional: si querés ocultar estas leyendas)
        private static readonly Regex RxMonthYearLine = new(
            @"(?mi)^\s*(enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|octubre|noviembre|diciembre)\s+\d{4}\s*$",
            RegexOptions.Compiled);

        // --- Pegar junto a tus otras Regex / helpers (mismo nivel que RxStrictAmount, etc.)

        private static readonly Regex RxHeaderStart = new(
            @"Datos\s+de\s+la\s+cuenta",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxHeaderEnd = new(
            @"\b(Movimientos|Fecha\s+Descripci[oó]n)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxDate = new(
            @"\b\d{2}/\d{2}/(?:\d{2}|\d{4})\b",
            RegexOptions.Compiled);

        private static readonly Regex RxMoneyHeader = new(
            @"\$?\s*-?\s*\d{1,3}(?:[ \.]\d{3})*,\s*\d\s*\d\s*-?",
            RegexOptions.Compiled);

        // Avisos que marcan el fin del encabezado útil
        private static readonly Regex RxHeaderStopNotice = new(
            @"Dispon[eé]s de 30 d[ií]as.*?resumen\.|El cr[eé]dito fiscal discriminado.*?impositivos\.",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

        // Limpia y parsea importes ARS con coma decimal y '-' sufijo o prefijo
        private static decimal? ParseArs(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();

            var neg = false;
            if (s.EndsWith("-")) { neg = true; s = s[..^1]; }
            if (s.StartsWith("-")) { neg = true; s = s[1..]; }

            s = s.Replace("$", "").Replace(" ", "");
            s = s.Replace(".", "");       // miles
            s = s.Replace(",", ".");      // decimales

            if (decimal.TryParse(s, System.Globalization.NumberStyles.Number,
                                 System.Globalization.CultureInfo.InvariantCulture, out var d))
                return neg ? -d : d;

            return null;
        }

        private static string SliceHeaderZone(string page1Text)
        {
            if (string.IsNullOrWhiteSpace(page1Text)) return page1Text;

            var start = RxHeaderStart.Match(page1Text);
            var end = RxHeaderEnd.Match(page1Text);

            int i0 = start.Success ? start.Index : 0;
            int i1 = end.Success && end.Index > i0 ? end.Index : page1Text.Length;

            // cortar antes de los avisos legales SIEMPRE, incluso en fallback
            var stop = RxHeaderStopNotice.Match(page1Text, i0);
            if (stop.Success && stop.Index > i0 && stop.Index < i1)
                i1 = stop.Index;

            return page1Text.Substring(i0, i1 - i0);
        }

        private static (decimal? opening, decimal? closing)
        ExtractHeaderBalancesByProximity(string page1Text, int proximityWindow = 80)
        {
            var header = SliceHeaderZone(page1Text);
            if (string.IsNullOrWhiteSpace(header)) header = page1Text; // fallback por si no encontró marcadores
            var hdr = TightenForNumbers(header);

            // Todas las fechas y montos con su posición
            var dates = RxDate.Matches(header).Cast<Match>().ToList();
            var moneys = RxMoneyHeader.Matches(header).Cast<Match>().ToList();

            if (dates.Count < 2 || moneys.Count == 0) return (null, null);

            // Identificar cuál de las dos fechas es inicio y cuál fin
            DateTime d1, d2;
            TryParseDate2Or4(dates.First().Value, out d1);
            TryParseDate2Or4(dates.Last().Value, out d2);
            var startDate = d1 <= d2 ? dates.First() : dates.Last();
            var endDate = d1 <= d2 ? dates.Last() : dates.First();

            Match? ClosestMoneyTo(Match date) =>
                    moneys.Select(m => (m, dist: Math.Abs(m.Index - date.Index)))
                          .Where(x => x.dist <= proximityWindow)
                          .OrderBy(x => x.dist).Select(x => x.m).FirstOrDefault()
                    ?? moneys.OrderBy(m => Math.Abs(m.Index - date.Index)).FirstOrDefault();

            var openM = ClosestMoneyTo(startDate);
            var closeM = ClosestMoneyTo(endDate);

            // Si ambos apuntan al mismo match, invalida apertura
            if (openM is not null && closeM is not null && openM.Index == closeM.Index) openM = null;

            var opening = ParseArs(openM?.Value ?? "");
            var closing = ParseArs(closeM?.Value ?? "");

            // Solo aceptar apertura positiva y cierre negativo
            if (opening is null || opening <= 0) opening = null;
            if (closing is null || closing >= 0) closing = null;

            return (opening, closing);
        }

        // Antes se llamaba ExtractOpeningClosingFromPage1; renombrado para claridad
        private static (decimal? opening, decimal? closing) ExtractOpeningClosingDollar(string page1)
        {
            if (string.IsNullOrWhiteSpace(page1)) return (null, null);

            var headerRaw = SliceHeaderZone(page1);
            var header = TightenForNumbers(headerRaw);

            decimal? opening = null;
            decimal? closing = null;

            var dollarMatches = RxDollarAmount.Matches(header);

            var dates = RxAnyDate.Matches(header);
            Match? endDate = dates.Count > 0
                ? dates.Cast<Match>().OrderBy(m =>
                {
                    TryParseDate2Or4(m.Value, out var d); return -d.Ticks; // mayor = fin
                }).First()
                : null;

            var dollars = RxDollarAmount.Matches(header).Cast<Match>().ToList();

            // Cierre = $ con sufijo '-'; preferir el más cercano a la fecha de fin
            var negs = dollars.Where(m => m.Groups["neg"].Success).ToList();
            if (negs.Count > 0)
            {
                var pick = endDate is null
                    ? negs[0]
                    : negs.OrderBy(m => Math.Abs(m.Index - endDate.Index)).First();
                try { closing = ParseSaldo(pick.Groups["m"].Value + "-"); } catch { }
            }

            // Apertura = $ positivo cerca de "Saldos"
            var idx = header.IndexOf("Saldos", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var slice = header.Substring(idx, Math.Min(300, header.Length - idx));
                foreach (Match m in RxDollarAmount.Matches(slice))
                    if (!m.Groups["neg"].Success && TryParseDecimalEs(m.Groups["m"].Value, out var v)) { opening = v; break; }
            }

            // Fallback: mayor $ positivo del header
            if (!opening.HasValue)
            {
                decimal? maxPos = null;
                foreach (var m in dollars)
                    if (!m.Groups["neg"].Success && TryParseDecimalEs(m.Groups["m"].Value, out var v))
                        if (maxPos is null || v > maxPos) maxPos = v;
                opening = maxPos;
            }

            return (opening, closing);
        }

        private static (decimal? opening, decimal? closing) ExtractHeaderBalances(string page1Text)
        {
            var (opProx, clProx) = ExtractHeaderBalancesByProximity(page1Text, proximityWindow: 80);
            var (opDollar, clDollar) = ExtractOpeningClosingDollar(page1Text);

            var opening = opProx ?? opDollar;
            var closing = clDollar ?? clProx;

            if (opening.HasValue && closing.HasValue && Math.Abs(opening.Value - closing.Value) < 0.005m)
                opening = null;               // => se calcula con txs
            if (opening.HasValue && opening.Value <= 0) opening = null;

            return (opening, closing);
        }

        private static readonly Regex RxAnyAmount =
            new Regex(@"-?\d{1,3}(?:\.\d{3})*,\d{2}-?", RegexOptions.Compiled);
        // Dinero "laaaaxo" para localizar el 1.º y 2.º monto en el texto crudo (permite espacios)
        private static readonly Regex RxMoneyLoose = new(@"\-?\d{1,3}(?:[.\s]\d{3})*,\d{2}\-?", RegexOptions.Compiled);

        // Filtros de “ruido de Origen”
        private static readonly Regex RxAllDigitsish = new(@"^[\d\s.\-]+$", RegexOptions.Compiled);
        private static readonly Regex RxCuit = new(@"\b\d{2}-\d{7,8}-\d\b", RegexOptions.Compiled);
        private static readonly Regex RxLongId = new(@"\b\d{10,}\b", RegexOptions.Compiled); // doc-id, CBU, tarjetas, etc.
        private static readonly string[] DropTokens = { "PROPIA", "VARIOS", "CUENTA ORIGEN" /* podés sumar más acá */ };

        private static readonly Regex RxLongDigits =
            new Regex(@"\b\d{10,}\b", RegexOptions.Compiled);

        private static readonly Regex RxMostlyDigits =
            new Regex(@"^\s*[\d\s\.\-]+$", RegexOptions.Compiled);

        private static readonly Regex RxCodey =
            new Regex(@"^[A-Z]{2,}\d{3,}$", RegexOptions.Compiled);

        // artefacto típico por OCR (“ -1” escapado del importe negativo)
        private static readonly Regex RxStrayMinusDigit =
            new Regex(@"(?<=\s)-\d{1,3}(?=\s|$)", RegexOptions.Compiled);

        // pistas de “Origen” que sí nos interesan
        private static readonly string[] OriginKeepHints = new[] {
            "HABERES","ACRED.HABERES",
            "PROVEEDORES",
            "FIMA PREMIUM",
            "REG.RECAU.SIRCREB",
            "AFIP","PLANRG",
            "D.A. AL VTO","BUSINESS",
            "BANCO","SANTANDER","NUEVO BANCO",
            "MERCANTIL","HONORARIOS"
        };

        private static bool ShouldKeepOriginLine(string s)
        {
            var t = s.Trim();
            if (t.Length == 0) return false;
            if (RxAnyAmount.IsMatch(t)) return false;          // fuera montos
            if (RxCuit.IsMatch(t)) return false;               // fuera CUIT
            if (RxLongDigits.IsMatch(t)) return false;         // largas tiras de números/IDs
            if (RxMostlyDigits.IsMatch(t)) return false;
            if (RxCodey.IsMatch(t)) return false;              // “PROD12345”, etc.
            if (t.Contains("0000000000")) return false;        // ceros infinitos (CBUs/refs)
                                                               // Mantener si contiene alguna pista útil de origen / banco / concepto
            return OriginKeepHints.Any(h => t.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                   || t.Split(' ').Count(w => w.Length > 1) >= 1;
        }

        private static string PostClean(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // 1) artefactos del parser/servicio
            s = s.Replace("@@@", " ");

            // 2) espacios múltiples y guiones sueltos
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            s = Regex.Replace(s, @"\s*—\s*—\s*", " — "); // doble em-dash -> uno
            s = Regex.Replace(s, @"\s*·\s*·\s*", " · "); // doble punto intermedio -> uno

            // 3) “-1” sueltos que se cuelan de signos de saldo negativos
            s = Regex.Replace(s, @"(?<=\s|^)-1(?=\s|$)", "");

            // 4) limpia separadores en extremos
            s = s.Trim(' ', '—', '·', '-');

            // 5) colapsa espacios otra vez por si quedaron huecos
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            return s;
        }

        // encabezados/pies/columnas que NO queremos como extra
        private static readonly string[] DropStarts = {
            "Página", "<<PAGE:", "Resumen de ", "CBU", "N° ", "Credito", "Crédito", "Débito", "Saldo"
        };

        // tokens genéricos de Origen a descartar
        private static readonly string[] DropExact = {
            "PROPIA", "VARIOS", "CUENTA ORIGEN", "PROVEEDORES",
            "ACRED.HABERES", "REG.RECAU.SIRCREB"
        };

        // nombres útiles que SÍ queremos dejar pasar aunque estén en mayúsculas
        private static bool LooksLikeUsefulName(string line)
        {
            // Heurística: dos o más palabras con letras, o contiene “FIMA”, “CLASE”, “PREMIUM”
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool twoWordsWithLetters = words.Count(w => w.Any(char.IsLetter)) >= 2;
            bool fundHints = line.Contains("FIMA", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("CLASE", StringComparison.OrdinalIgnoreCase)
                          || line.Contains("PREMIUM", StringComparison.OrdinalIgnoreCase);
            return twoWordsWithLetters || fundHints;
        }

        private static List<string> ExtractExtraDetailLines(string blockLarge, int blockStart, int blockEnd, int maxLines)
        {
            // localizar el 2.º monto (saldo) en el bloque crudo y tomar desde ahí
            var moneys = RxMoneyLoose.Matches(blockLarge);
            if (moneys.Count < 2) return new();

            int from = moneys[1].Index + moneys[1].Length;
            if (from < 0 || from >= blockLarge.Length) return new();

            int len = Math.Max(0, Math.Min(blockEnd - blockStart, blockLarge.Length - from));
            var tail = blockLarge.Substring(from, len);

            var keep = new List<string>(maxLines);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in tail.Split('\n'))
            {
                var l0 = raw.Trim();
                if (l0.Length == 0) continue;

                // limpieza básica (@@@ y espacios) antes de decidir
                var l = PostClean(l0);
                if (l.Length == 0) continue;

                // 1) descartar obvios de encabezado/pie/columnas
                if (DropStarts.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                // 2) descartar ruido de Origen/códigos
                if (DropExact.Any(t => string.Equals(l, t, StringComparison.OrdinalIgnoreCase))) continue;
                if (RxAllDigitsish.IsMatch(l)) continue;
                if (RxCuit.IsMatch(l)) continue;
                if (RxLongId.IsMatch(l)) continue;

                // 3) quedarnos con líneas "humanas"
                if (!l.Any(char.IsLetter)) continue;
                if (!LooksLikeUsefulName(l)) continue;

                // 4) evitar duplicados / repeticiones con distinta puntuación
                var norm = Regex.Replace(l, @"\s+", " ").Trim();
                if (seen.Contains(norm)) continue;
                seen.Add(norm);

                keep.Add(norm);
                if (keep.Count >= maxLines) break;
            }

            return keep;
        }

        // Normaliza todos los “dash-like” Unicode a '-' ASCII
        private static string NormalizeDashes(string s) =>
            string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]", "-");

        // ===== Utils =====
        private static string StripDelimEnd(string s) =>
            string.IsNullOrEmpty(s) ? string.Empty : (s.EndsWith("@@@") ? s[..^3] : s);

        // Normalización solo para DETECCIÓN de montos (no altera Description/OriginalDescription)
        private static string TightenForNumbers(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;


            // 0. Normalizar TODOS los espacios Unicode a ' ' (ASCII)
            //    \p{Zs} = Space_Separator (incluye NBSP, NNBSP, thin space, etc.)
            var t = Regex.Replace(s, @"\p{Zs}+", " ");
            t = t.Replace('\u00A0', ' '); // NBSP (por las dudas)
            t = NormalizeDashes(t);


            // 0) Pegar un '-' que quedó separado del monto por espacios/salto/@@@
            // Flatten @@@ y saltos para no cortar tokens numéricos
            t = Regex.Replace(t, @"(?:@{3}|\r?\n)", " ");

            // 0.b) Si el '-' quedó pegado a una palabra (p.ej. "ING.BRUTOS- 81.234,56"),
            //      separarlo de la palabra para que luego podamos unir "- " con el monto.
            t = Regex.Replace(t,
                @"(?<=[\p{L}\p{Nd}])-(?=\s*\d{1,3}(?:\.\d{3})*,\d{2}\b)", " -");

            // 0.c Convertir @@@ y saltos a un solo espacio para no romper tokens
            t = Regex.Replace(t, @"(?:@{3}|\r?\n)", " ");

            // 1) Quitar espacios alrededor de puntos y comas
            t = Regex.Replace(t, @"\s*\.\s*", ".");
            t = Regex.Replace(t, @"\s*,\s*", ",");

            // 1.b) Asegurar punto de miles cuando hay " . " entre dígitos: "41 . 107,30" -> "41.107,30"
            t = Regex.Replace(
                t,
                @"(?<=\d)\s*\.\s*(?=\d{3},\d{2}\b)",
                "."
            );

            // 1.c) Si quedó un espacio inmediatamente antes de la coma de decimales, quitarlo
            t = Regex.Replace(t, @"(?<=\d)\s+(?=,\d{2}\b)", "");

            // 2.a) Centavos partidos: ",1 1" -> ",11"
            t = Regex.Replace(t, @",(?<a>\d)\s+(?<b>\d)\b", @",${a}${b}");

            // 2.a2) Entero "cortado" justo antes de la coma (1 o 2 dígitos):
            // "-41 1 ,68" / "-41 11 ,68" -> "-411,68"
            t = Regex.Replace(
                t,
                @"(?<=^|\s)-\s*(?<head>\d{1,3})\s+(?<tail>\d{1,2})\s*,\s*(?<c1>\d)\s*(?<c2>\d)\b",
                "-${head}${tail},${c1}${c2}"
            );

            // 2.a3) Igual que arriba pero si ya limpiaste espacios alrededor de la coma:
            // "-41 1,68" / "-41 11,68" -> "-411,68"
            t = Regex.Replace(
                t,
                @"(?<=^|\s)-\s*(?<head>\d{1,3})\s+(?<tail>\d{1,2}),(?<c1>\d)(?<c2>\d)\b",
                "-${head}${tail},${c1}${c2}"
            );

            // 2.b) Glitch OCR: "-1 1.996.000,00" -> "-11.996.000,00"
            t = Regex.Replace(t, @"-(\d)\s+(?=\1(?:\.\d{3}){1,3},\d{2}\b)", "-$1");

            // 2.b1) OCR: "-1 16.350,00"  -> "-116.350,00"
            // quita SOLO el espacio justo después de "-1" si viene un monto con miles y decimales
            t = Regex.Replace(t,
                @"(?<=-1)\s+(?=\d{2}\.\d{3},\d{2}\b)", "");

            // 2.b2) OCR: "18.1 10,00" -> "18.110,00"  (último grupo de miles roto)
            // acepta 1 o 2 dígitos antes de la coma porque puede venir "1 10,00", "11 0,00", etc.
            t = Regex.Replace(t,
                @"(?<=\.\d{1,3})\s+(?=\d{1,2},\d{2}\b)", "");

            // 2.b3) OCR entre grupos de miles: "6.01 1.703,10" -> "6.011.703,10"
            t = Regex.Replace(t,
                @"(?<=\.\d{2,3})\s+(?=\d\.\d{3}(?:\.\d{3})*,\d{2}\b)", "");

            // 2.b3 REFORZADO: "29.11 1.536,60" -> "29.111.536,60"
            t = Regex.Replace(t,
                @"(?<=\.\d{1,3})\s+(?=\d(?:\.\d{3}){1,6},\d{2}\b)", "");

            // 2.b3 REFORZADO BIS) Entre grupos de miles puede venir espacio / salto / @@@
            //    "29.11 1.536,60" o "29.11@@@1.536,60" -> "29.111.536,60"
            t = Regex.Replace(t,
                @"(?<=\.\d{1,3})\s*(?:@@@|\r?\n)?\s+(?=\d\.\d{3}(?:\.\d{3})*,\d{2}\b)",
                "");

            // 2.b4) Último grupo de miles espacio-separado antes de la coma: "21.700.1 1 1,85" -> "21.700.111,85"
            t = Regex.Replace(t,
                @"(?<=\.)((?:\d\s+){1,2}\d)(?=,\d{2}\b)",
                m => m.Value.Replace(" ", ""));

            // 2.b5) Variante cuando hay 2 espacios rotos: "24.0 1 1,36" -> "24.011,36"
            t = Regex.Replace(t,
                @"(?<=\.\d)\s+(?=\d(?:\s+\d)?,\d{2}\b)", "");

            // 2.c) Espacio roto en el último grupo de miles antes de la coma
            //      "24.01 1,36" -> "24.011,36"  (ahora acepta 1–3 dígitos antes de la coma)
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d{1,3},\d{2}\b)", "");

            // 2.c1) Espacio roto entre el último dígito de miles y el primer dígito del grupo siguiente,
            //       p.ej. "-1.1 15,95" -> "-1.115,95" (no exige punto tras el dígito de la derecha)
            t = Regex.Replace(t, @"(?<=\.\d{1,3})\s+(?=\d{1,3}(?:,|\.\d{3},)\d{2}\b)", "");

            // 2.c2) Si quedó un espacio justo después de "-1" y antes de una secuencia con miles+decimales,
            //       cubrimos también montos con 1–3 dígitos de cabeza: "-1 115,95" -> "-1115,95"
            t = Regex.Replace(t, @"(?<=-1)\s+(?=\d{1,3}(?:\.\d{3})*,\d{2}\b)", "");

            // 2.d) "1 15.315,81" -> "115.315,81" (un dígito suelto + grupo de 2 dígitos)
            t = Regex.Replace(t,
                @"(?<=\b\d)\s+(?=\d{2}\.\d{3},\d{2}\b)", "");

            // 2.d1) Dígito suelto + grupo de 2 dígitos antes del primer punto de miles
            //    "1 15.315,81" o "1@@@15.315,81" -> "115.315,81"
            t = Regex.Replace(t,
                @"(?<=\b\d)\s*(?:@@@|\r?\n)?\s+(?=\d{2}\.\d{3},\d{2}\b)",
                "");

            // 2.e) "1 536,60" -> "1.536,60" (faltaba el primer punto de miles)
            t = Regex.Replace(t,
                @"(?<=\b\d{1,3})\s+(?=\d{3},\d{2}\b)", ".");

            // 2.e1) Faltaba el primer punto de miles (con separador raro en el medio)
            //    "1 536,60" o "1@@@536,60" -> "1.536,60"
            t = Regex.Replace(t,
                @"(?<=\b\d{1,3})\s*(?:@@@|\r?\n)?\s+(?=\d{3},\d{2}\b)",
                ".");

            // 2.d) Signo + entero + coma + centavos partidos por espacios -> pegarlos
            //     "-41 @@@ , 7 1" -> "-41,71"   |   "- 333 , 4 1" -> "-333,41"
            t = Regex.Replace(t,
                @"(?<=^|\s)-\s*(?<ent>\d{1,3})\s*,\s*(?<a>\d)\s*(?<b>\d)\b",
                "-${ent},${a}${b}");

            // 2.e) Entre grupos de miles: "29.11 1.536,60" (o con @@@/saltos) -> "29.111.536,60"
            t = Regex.Replace(t,
                @"(?<=\.\d{1,3})\s+(?=\d\.\d{3}(?:\.\d{3})*,\d{2}\b)", "");

            // 2.f) Dígito(s) sueltos antes de un monto grande: "1 15.315,81" -> "115.315,81"
            //     (también cubre "11 5.315,81" -> "115.315,81")
            t = Regex.Replace(t,
                @"(?<=^|\s)(?<lead>\d{1,2})\s+(?=\d{1,3}(?:\.\d{3}){1,3},\d{2}(?:-|\b))",
                "${lead}");

            // 3) Débitos con espacio tras el signo: "- 333,41" -> "-333,41"
            t = Regex.Replace(t, @"-\s+(?=\d)", "-");

            // Signo de débito con espacios entre medio: "- 333,41" / "-   333,41" -> "-333,41"
            t = Regex.Replace(t, @"(?<=^|\s)-\s{0,3}(?=\d{1,3}(?:\.\d{3})*,\d{2}\b)", "-");

            // 4) Saldos negativos con espacio antes del sufijo: "99 -" -> "99-"
            t = Regex.Replace(t, @"(?<=\d)\s+-\b", "-");

            // 5) Colapsar espacios largos
            t = Regex.Replace(t, @"[ \t]{2,}", " ");

            // Centavos partidos: ",1 1" / ",5 6" -> ",11" / ",56"  (ya lo tenías, lo dejo para énfasis)
            t = Regex.Replace(t, @",(?<a>\d)\s+(?<b>\d)\b", @",${a}${b}");

            // Miles con uno o dos espacios antes de la coma: "… .01 1,36" / "… .1 10,00" -> sin espacios
            t = Regex.Replace(t, @"(?<=\.\d{1,2})\s+(?=\d,\d{2}\b)", "");

            return t;
        }

        private static string FixInlineMinusBeforeMoney(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = NormalizeDashes(s);

            // 25413-83.351,31  ->  25413 -83.351,31  (evita que ese '-' se tome como signo)
            return Regex.Replace(
                s,
                @"(?<=[\p{L}\p{Nd}])-(?=\s*\d{1,3}(?:\s?\.\s?\d{3})*,\s?\d{2}(?:-|\b))",
                " -"
            );
        }


        private static string NormalizeMoneyToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            // Unificar espacios (incluyendo NBSP) y quitarlos
            s = s.Replace('\u00A0', ' ');
            s = Regex.Replace(s, @"\s+", "");
            return s;
        }

        // Núcleo “estricto”: solo x,xx | xx,xx | xxx,xx | x.xxx,xx | xx.xxx,xx | ...
        private static readonly Regex RxStrictAmountCore = new Regex(
            @"^\d{1,3}(?:\.\d{3})*,\d{2}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        private static (string? accountDigits, string? cbu, string? cuit) ExtractAccountMetaFromPage1(string page1)
        {
            string? acc = null, cbu = null, cuit = null;

            var mAcc = RxAccountNumber.Match(page1);
            if (mAcc.Success)
            {
                var raw = mAcc.Groups["raw"].Value;
                acc = new string(raw.Where(char.IsDigit).ToArray());
                if (string.IsNullOrWhiteSpace(acc)) acc = null;
            }

            var mCBU = RxCBU.Match(page1);
            if (mCBU.Success) cbu = mCBU.Groups["cbu"].Value;

            var mCUIT = RxCUIT.Match(page1);
            if (mCUIT.Success) cuit = mCUIT.Groups["cuit"].Value;

            return (acc, cbu, cuit);
        }

        // Helper seguro para "ES" (miles con '.' y decimales con ',')
        private static bool TryParseDecimalEs(string s, out decimal value)
        {
            var norm = (s ?? string.Empty).Replace(".", "").Replace(",", ".");
            return decimal.TryParse(norm, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryParseDateDdMmYy(string s, out DateTime d)
        {
            var clean = Regex.Replace(s ?? "", @"\s+", "");   // quita espacios dentro de "1 1/04/23"
            clean = Regex.Replace(clean, @"\s*/\s*", "/");    // normaliza barras
            return DateTime.TryParseExact(clean, "dd/MM/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }

        private static bool TryParseDate2Or4(string s, out DateTime d)
        {
            var norm = Regex.Replace(s, @"\s*/\s*", "/");
            string[] fmts = { "dd/MM/yy", "dd/MM/yyyy" };
            return DateTime.TryParseExact(norm, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }

        private static bool IsStrictMoney(string tokenCore /* sin signo */)
        {
            var t = NormalizeMoneyToken(tokenCore);
            return RxStrictAmountCore.IsMatch(t);
        }

        private static decimal ParseImporte(string token)
        {
            var s = NormalizeDashes(token).Trim();
            bool neg = s.StartsWith("-");
            s = s.TrimStart('-');
            if (!IsStrictMoney(s)) throw new FormatException($"Monto (importe) fuera de formato estricto: '{token}'");
            var val = decimal.Parse(s.Replace(".", "").Replace(",", "."), CultureInfo.InvariantCulture);
            return neg ? -val : val;
        }

        private static decimal ParseSaldo(string token)
        {
            var s = NormalizeDashes(token).Trim();
            bool neg = s.EndsWith("-");
            s = s.TrimEnd('-');
            if (!IsStrictMoney(s)) throw new FormatException($"Monto (saldo) fuera de formato estricto: '{token}'");
            var val = decimal.Parse(s.Replace(".", "").Replace(",", "."), CultureInfo.InvariantCulture);
            return neg ? -val : val;
        }

        private static string ExtractPageText(string full, int page)
        {
            var tag = $"<<PAGE:{page}>>>";
            var idx = full.IndexOf(tag, StringComparison.Ordinal);
            if (idx < 0) return string.Empty;
            var from = idx + tag.Length;
            var next = full.IndexOf("<<PAGE:", from, StringComparison.Ordinal);
            return next >= 0 ? full[from..next] : full[from..];
        }

        private static (DateTime? start, DateTime? end) ExtractPeriodFromPage1(string page1)
        {
            var ms = RxAnyDate.Matches(page1);
            if (ms.Count < 2) return (null, null);

            var parsed = new List<DateTime>();
            foreach (Match m in ms)
            {
                if (TryParseDate2Or4(m.Groups["d"].Value, out var d))
                    parsed.Add(d);
            }
            if (parsed.Count < 2) return (null, null);
            parsed.Sort();
            return (parsed[0], parsed[^1]);
        }

        // Toma closing = $…- ; opening = $… positivo próximo a "Saldos" o, si no, el mayor $ positivo de la portada
        private static string Trunc(string s, int n) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= n ? s : s.Substring(0, n) + " …[truncated]");

        // Helper para limpiar texto para warnings
        private static string TrimForWarn(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var cleaned = s.Replace("\n", " ").Replace("\r", " ");
            cleaned = Regex.Replace(cleaned, @"\s+", " ");
            return Trunc(cleaned, 180);
        }

        private static readonly HashSet<string> OrigenStopWords =
        new(StringComparer.OrdinalIgnoreCase)
            {
                "PROPIA", "VARIOS", "HABERES", "ACRED.HABERES", "ACRED. HABERES",
                "CUENTA ORIGEN", "CUENTA ORIGEN CAJA A", "ORIGEN", "SOBRE SALDOS"
            };

        private static readonly Regex RxOnlyDigitsLong = new(@"^\s*\d{6,}\s*$", RegexOptions.Compiled);
        private static readonly Regex RxZerosLong = new(@"^\s*0{6,}\s*$", RegexOptions.Compiled);
        private static readonly Regex RxCUITLine = new(@"^\s*\d{2}-\d{8}-\d\s*$", RegexOptions.Compiled);
        private static readonly Regex RxCBULine = new(@"^\s*\d{22}\s*$", RegexOptions.Compiled);
        private static readonly Regex RxCardLike = new(@"^\s*\d{13,19}\s*$", RegexOptions.Compiled);

        private const decimal EPS = 0.05m; // tolerancia centavos/rounding

        private static bool NearlyEq(decimal a, decimal b, decimal eps = EPS)
            => Math.Abs(a - b) <= eps;

        private static (decimal fixedAmount, string? reason) ReconcileAmount(
            decimal parsedAmount,
            decimal currentBalance,
            decimal? prevBalance)
        {
            if (!prevBalance.HasValue)
                return (parsedAmount, null);

            var expected = currentBalance - prevBalance.Value;

            // OK tal cual
            if (NearlyEq(parsedAmount, expected))
                return (parsedAmount, null);

            // ¿solo error de signo?
            if (NearlyEq(-parsedAmount, expected))
                return (-parsedAmount, "flip_sign");

            // Desvío grande (e.g. “402.004.728,00” vs “2.004.728,00”) → forzamos al esperado
            // Podés afinar los umbrales si querés
            var bigGap = Math.Abs(parsedAmount - expected) > 10m && Math.Abs(parsedAmount) > 50m;
            if (bigGap)
                return (expected, "force_expected");

            // Último recurso: forzar esperado si no cierra
            return (expected, "force_expected_soft");
        }


        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress = null)
        {
            var result = new ParseResult
            {
                Statement = new BankStatement { Bank = "Banco Galicia", Accounts = new List<AccountStatement>() },
                Warnings = new List<string>()
            };

            var full = (text ?? string.Empty).Replace("\r\n", "\n");
            // Instrumentation counters
            int blocksParsedCount = 0;
            int blocksSkippedNo2Count = 0;
            int fallbackUsedCount = 0;
            int pageCutRetriedCount = 0;
            int dashTrimmedBeforeMoneyCount = 0;


            // 1.a) Unifica espacios alrededor de las barras en dd/MM/yy
            full = Regex.Replace(
                full,
                @"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2})\s*/\s*(\d{2})(?!\d)",
                "$1/$2/$3",
                RegexOptions.Compiled);

            // 1.b) Si una fecha aparece “en medio de línea”, forzá salto ANTES
            full = Regex.Replace(
                full,
                @"(?<![\r\n])(?<!^)(?<!\d)(\d{1,2}/\d{1,2}/\d{2})",
                "\n$1",
                RegexOptions.Compiled);

            // 1.c) OCR típico: día con dígito duplicado separado "1 1/04/23" -> "11/04/23"
            full = Regex.Replace(
                full,
                @"(?<!\d)(\d)\s+(?=\1/\d{2}/\d{2})",
                "$1",
                RegexOptions.Compiled);

            // Auditoría: snapshots
            var page1 = ExtractPageText(full, 1);
            var (openingP1, closingP1) = ExtractHeaderBalances(page1);


            if (!string.IsNullOrWhiteSpace(page1))
                result.Warnings.Add("[raw.page1] " + Trunc(page1, 1200));
            result.Warnings.Add("[raw.sample] " + Trunc(full, 1000));

            // Meta de portada
            var (pStart, pEnd) = ExtractPeriodFromPage1(page1);
            var headerDbg = SliceHeaderZone(page1);
            result.Warnings.Add("[debug.page1_header] " + Trunc(headerDbg, 600));

            var moneyInHeader = RxMoneyHeader.Matches(headerDbg);
            result.Warnings.Add("[debug.header_moneys] " + string.Join(" | ", moneyInHeader.Select(m => m.Value)));

            // NUEVO: meta de cuenta
            var (accDigits, cbu, cuit) = ExtractAccountMetaFromPage1(page1);
            if (!string.IsNullOrEmpty(accDigits)) result.Warnings.Add($"[meta.accountNumberDigits] {accDigits}");
            if (!string.IsNullOrEmpty(cbu)) result.Warnings.Add($"[meta.cbu] {cbu}");
            if (!string.IsNullOrEmpty(cuit)) result.Warnings.Add($"[meta.cuit] {cuit}");

            var account = new AccountStatement();
            if (!string.IsNullOrEmpty(accDigits)) account.AccountNumber = accDigits;
            result.Statement.Accounts.Add(account);

            // Hallar anclas de fecha por línea (multilínea)
            var matches = RxDateLineAnchor.Matches(full);
            result.Warnings.Add($"[debug.blocks_found] {matches.Count}");

            result.Warnings.Add("[debug.date_lines] " +
            string.Join(" | ", matches.Select(m => Regex.Replace(m.Groups["d"].Value, @"\s*", ""))));


            // Si no hay fechas, devolvemos pronto con warning
            if (matches.Count == 0)
            {
                result.Warnings.Add("No se detectaron líneas con fecha al inicio.");
                if (pStart.HasValue && pEnd.HasValue)
                {
                    result.Statement.PeriodStart = pStart.Value;
                    result.Statement.PeriodEnd = pEnd.Value;
                }

                // Opening/Closing: preferir extracción por "$ ..."/"$ ...-" de portada;
                // si no están, caer a proximidad por fechas del header.
                account.OpeningBalance = openingP1;
                account.ClosingBalance = closingP1;

                return result;
            }

            decimal? prevRunningBalance = openingP1; // si no hay openingP1, se concilia desde la 2ª línea

            // Recorrer bloques [fecha_i .. fecha_{i+1})
            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;

                // 1) Próxima fecha (tope natural)
                int endNextDate = (i + 1 < matches.Count) ? matches[i + 1].Index : full.Length;

                // 2) (YA NO) cortar por "Total $" ni por "Consolidado ..."
                //    int totalIdx = full.IndexOf("Total $", start, StringComparison.OrdinalIgnoreCase);
                //    int consIdx  = full.IndexOf("Consolidado de retención", start, StringComparison.OrdinalIgnoreCase);

                int endCandidate = endNextDate;

                // 3) Intento 1: respetando salto de página si cae antes
                int nextPageIdx = full.IndexOf("<<PAGE:", start, StringComparison.Ordinal);
                bool cutByPage = nextPageIdx >= 0 && nextPageIdx < endCandidate;

                string blockLarge = full.Substring(start, (cutByPage ? nextPageIdx : endCandidate) - start);

                // --- NORMALIZAR + buscar montos (intento 1)
                var normalized = TightenForNumbers(blockLarge);
                var amts = RxStrictAmount.Matches(normalized);

                // 4) Si no hay 2 montos y el corte fue por página, reintentar sin corte de página
                if (amts.Count < 2 && cutByPage) { pageCutRetriedCount++; }
                if (amts.Count < 2 && cutByPage)
                {
                    blockLarge = full.Substring(start, endCandidate - start);
                    normalized = TightenForNumbers(blockLarge);
                    amts = RxStrictAmount.Matches(normalized);
                }

                if (amts.Count < 2)
                {
                    result.Warnings.Add($"[debug.skipped_no_2_amounts_at] idx={start} text='{TrimForWarn(normalized)}'");
                    continue;
                }

                int cutEndNorm = amts[1].Index + amts[1].Length;
                var blockNormCut = normalized.Substring(0, cutEndNorm);

                // ➜ NUEVO: convertir @@@ a saltos de línea ANTES de limpiar renglones
                var blockForDesc = blockNormCut.Replace("@@@", "\n");

                // usá blockForDesc (no blockNormCut) para la limpieza de renglones
                var blockDesc = string.Join("\n",
                    blockForDesc
                        .Split('\n')
                        .Select(StripDelimEnd)
                        .Where(line =>
                            !line.TrimStart().StartsWith("<<PAGE:", StringComparison.OrdinalIgnoreCase) &&
                            !line.TrimStart().StartsWith("Página", StringComparison.OrdinalIgnoreCase) &&
                            !line.TrimStart().StartsWith("Total $", StringComparison.OrdinalIgnoreCase) &&
                            line.IndexOf("Consolidado de", StringComparison.OrdinalIgnoreCase) < 0
                        )
                ).TrimEnd();

                // Para debug transparente: guardo el crudo original SIN normalizar (tal como vino)
                var blockRawForDebug = blockLarge.TrimEnd();

                // --- A PARTIR DE ACÁ sigue tu lógica como ya la tenés ---
                // 1) Fecha, 2) Extraer montos (tomar SOLO los primeros 2),
                // 3) amount/balance con ToDecimalAllowingSpaces, 4) Tipo, 5) Resto como descripción
                // ...

                if (string.IsNullOrWhiteSpace(blockDesc)) continue;

                // DEBUG: ver bloque crudo vs normalizado (primeros 5)
                bool dumpThis =
                    (i < 5) || (i >= matches.Count - 5);

                if (dumpThis)
                {
                    var detectPreview = TightenForNumbers(blockDesc);
                    result.Warnings.Add($"[debug.block_pre_{i + 1}] " + Trunc(blockDesc.Replace("\n", " "), 300));
                    result.Warnings.Add($"[debug.block_post_{i + 1}] " + Trunc(detectPreview.Replace("\n", " "), 300));
                }

                // Fecha del bloque (debe estar al principio)
                var mDate = RxDateLineAnchor.Match(blockDesc);
                if (!mDate.Success || mDate.Index != 0) continue;
                if (!TryParseDateDdMmYy(mDate.Groups["d"].Value, out var date)) continue;

                // --- 2) Montos: tomar SIEMPRE los primeros 2 estrictos y delimitados ---
                var detect = TightenForNumbers(blockDesc);

                detect = FixInlineMinusBeforeMoney(detect);

                detect = Regex.Replace(detect, @"(?<=\s|^)-\s{0,3}(?=\d)", "-");

                var mDelim = RxMoneyDelimited.Matches(detect);

                var strict = new List<string>(2);
                foreach (Match m in mDelim)
                {
                    var tok = m.Groups[1].Value.Trim();   // el token completo -?N.NNN,dd-?
                    var core = tok.Trim('-');


                    // ignorar $... de portada, por si aparece en algún bloque extraño
                    int prev = m.Index - 1;
                    while (prev >= 0 && char.IsWhiteSpace(detect[prev])) prev--;

                    // --- defensa contra "25413-83.351,31" ---
                    // SOLO si el '-' está PEGADO al token y PEGADO a alfanum a la izquierda
                    if (m.Index >= 2 && detect[m.Index - 1] == '-' && char.IsLetterOrDigit(detect[m.Index - 2]))
                    {
                        result.Warnings.Add("[dash-guard] trimmed hyphen before money near: " +
                            Trunc(detect.Substring(Math.Max(0, m.Index - 12),
                            Math.Min(24, detect.Length - Math.Max(0, m.Index - 12))), 60));
                        tok = tok.TrimStart('-');
                        dashTrimmedBeforeMoneyCount++;
                    }

                    if (!IsStrictMoney(core)) continue;

                    strict.Add(tok);
                    if (strict.Count == 2) break;
                }

                decimal amount, balance; bool usedFallbackThisBlock = false;

                if (strict.Count >= 2)
                {
                    var importeTok = strict[0];
                    var saldoTok = strict[1];

                    try
                    {
                        amount = ParseImporte(importeTok); // usa tu lógica actual
                        balance = ParseSaldo(saldoTok);
                    }
                    catch
                    {
                        // FALLBACK: dos últimos montos del bloque CRUDO (no normalizado)
                        var ab = TryParseAmountAndBalance(blockRawForDebug);
                        if (ab == null)
                        {
                            if (dumpThis)
                                result.Warnings.Add($"[debug.skipped_block_lt2_amounts] text='{Trunc(detect.Replace("\n", " "), 300)}'");
                            continue;
                        }
                        (amount, balance) = ab.Value; usedFallbackThisBlock = true; fallbackUsedCount++;
                    }
                }
                else
                {
                    // FALLBACK directo si no llegamos a 2 estrictos
                    var ab = TryParseAmountAndBalance(blockRawForDebug);
                    if (ab == null)
                    {
                        if (dumpThis)
                            result.Warnings.Add($"[debug.skipped_block_lt2_amounts] text='{Trunc(detect.Replace("\n", " "), 300)}'");
                        continue;
                    }
                    (amount, balance) = ab.Value; usedFallbackThisBlock = true; fallbackUsedCount++;
                }
                
                // --- Conciliación línea a línea con saldos ---
                var parsedAmount = amount;
                var (fixedAmount, reconReason) = ReconcileAmount(parsedAmount, balance, prevRunningBalance);

                if (reconReason is not null)
                {
                    // log de auditoría minimal
                    result.Warnings.Add(
                        $"[recon] i={i + 1} prev={(prevRunningBalance?.ToString("0.00") ?? "null")} " +
                        $"bal={balance:0.00} parsedAmt={parsedAmount:0.00} " +
                        $"=> fixedAmt={fixedAmount:0.00} reason={reconReason}"
                    );
                    amount = fixedAmount; // aplicar corrección
                }

                // actualizar saldo anterior para la próxima línea
                prevRunningBalance = balance;

                account.Transactions ??= new List<Transaction>();

                // 1) Descripción principal: desde fin de la fecha hasta el primer importe
                int firstAmtIdx = mDelim[0].Index;
                string mainDetect = detect.Substring(mDate.Length, Math.Max(0, firstAmtIdx - mDate.Length)).Trim();

                // Después (pasale el bloque crudo completo con todas las líneas):
                string displayDesc = DescriptionBuilder.BuildSmart(mainDetect ?? detect, blockRawForDebug ?? blockDesc);

                // Fallbacks
                if (string.IsNullOrWhiteSpace(displayDesc))
                    displayDesc = DescriptionBuilder.Build(blockRawForDebug ?? blockDesc);
                if (string.IsNullOrWhiteSpace(displayDesc))
                    displayDesc = PostClean(mainDetect ?? detect);

                account.Transactions.Add(new Transaction
                {
                    Date = date,
                    Description = displayDesc,
                    OriginalDescription = blockDesc,
                    Amount = amount,
                    Type = amount < 0 ? "debit" : "credit",
                    Balance = balance,
                    Category = null,
                    Subcategory = null,
                    CategorySource = null,
                    CategoryRuleId = null
                });
                // Instrumentation: per-block JSON
                try
                {
                    var blkLog = new
                    {
                        i = i + 1,
                        date = date.ToString("yyyy-MM-dd"),
                        cutByPage,
                        strictCount = strict.Count,
                        usedFallback = usedFallbackThisBlock,
                        amountToken = (strict.Count > 0 ? strict[0] : null),
                        balanceToken = (strict.Count > 1 ? strict[1] : null),
                        amount,
                        balance,
                        dashTrimmedBeforeMoneyCount,
                    };
                    result.Warnings.Add("[json.block] " + JsonSerializer.Serialize(blkLog));
                }
                catch { /* swallow instrumentation errors */ }

                blocksParsedCount++;

            }

            // Periodo (preferir portada)
            var txs = account.Transactions ?? new List<Transaction>();
            if (pStart.HasValue && pEnd.HasValue)
            {
                result.Statement.PeriodStart = pStart.Value;
                result.Statement.PeriodEnd = pEnd.Value;
            }
            else if (txs.Count > 0)
            {
                result.Statement.PeriodStart = txs.Min(t => t.Date);
                result.Statement.PeriodEnd = txs.Max(t => t.Date);
            }

            // Opening/Closing: NO pisar si ya están; preferir portada $... / $...- y
            // recién si faltan, usar la proximidad del header.
            if (openingP1.HasValue) account.OpeningBalance = openingP1.Value;
            if (closingP1.HasValue) account.ClosingBalance = closingP1.Value;


            // Fallback si faltan
            if (!account.OpeningBalance.HasValue && txs.Count > 0)
                account.OpeningBalance = txs[0].Balance - txs[0].Amount;
            if (!account.ClosingBalance.HasValue && txs.Count > 0)
                account.ClosingBalance = txs[^1].Balance;

            // Si no se extrajo nada, mostrar previews de los primeros bloques para debug
            if (txs.Count == 0 && matches.Count > 0)
            {
                for (int k = 0; k < Math.Min(matches.Count, 3); k++)
                {
                    int s = matches[k].Index;
                    int e = (k + 1 < matches.Count) ? matches[k + 1].Index : full.Length;
                    var blk = full.Substring(s, Math.Min(220, e - s)).Replace("\n", " ");
                    result.Warnings.Add($"[debug.block_preview_{k + 1}] {Trunc(blk, 220)}");
                }
            }

            // Chequeo suave de balances
            if (txs.Count > 0 && account.OpeningBalance.HasValue && account.ClosingBalance.HasValue)
            {
                var net = txs.Sum(t => t.Amount);
                var delta = account.OpeningBalance.Value + net - account.ClosingBalance.Value;
                if (Math.Abs(delta) > 0.02m)
                    result.Warnings.Add($"[balances] opening+Σ(amount) != closing (Δ={delta:0.00})");
            }


            // === Instrumentation summary ===
            result.Warnings.Add($"[summary.blocks] total={matches.Count} parsed={blocksParsedCount} skippedNo2={blocksSkippedNo2Count} fallbackUsed={fallbackUsedCount} pageCutRetried={pageCutRetriedCount} dashTrimmed={dashTrimmedBeforeMoneyCount}");
            return result;
        }
    }
}