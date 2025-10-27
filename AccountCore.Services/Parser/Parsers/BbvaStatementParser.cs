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
    /// Parser BBVA (Argentina) para texto OCR con separadores "@@@"
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

        private static string StripControls(string s) =>
            Regex.Replace((s ?? string.Empty).Replace('\u00A0', ' '), @"\p{C}+", " ");

        // Compacta: sin espacios, sin tildes, MAYÚSCULAS (para matching interno)
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
            var t = NormalizeDashes(token.Trim());
            t = Regex.Replace(t, @"^([+-])\s+(?=\d|\$)", "$1"); // "- 568,64" -> "-568,64" | "- $300" -> "-$300"
            t = Regex.Replace(t, @"\s*\.\s*", ".");
            t = Regex.Replace(t, @"\s*,\s*", ",");
            t = Regex.Replace(t, @"(?<=\d)\s+(?=\d)", "");
            t = Regex.Replace(t, @"\$\s+", "$");
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
            NormCompact(s).Contains("MOVIMIENTOSENCUENTAS");

        // ===================== Regex clave (tolerantes a espacios internos) =====================
        private static readonly Regex RxAmountSpaced = new(
            @"\$?\s*[+-]?\s*(?:\d\s*){1,3}(?:\.\s*(?:\d\s*){3})*\s*,\s*\d\s*\d",
            RegexOptions.Compiled);

        private static readonly Regex RxDdMmAny = new(
            @"(?<dd>\d\s*\d)\s*/\s*(?<mm>\d\s*\d)",
            RegexOptions.Compiled);

        // MÁS FLEXIBLE: captura el código de origen "D/C + 3 dígitos" o solo "3 dígitos" con basura intermedia
        private static readonly Regex RxOriginAfterDate = new(
            @"^\s*(?:[DC]\s*[\-:\.]?\s*)?(?<code>\d{3})\b",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, int> MonthEs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ENERO"] = 1,
            ["FEBRERO"] = 2,
            ["MARZO"] = 3,
            ["ABRIL"] = 4,
            ["MAYO"] = 5,
            ["JUNIO"] = 6,
            ["JULIO"] = 7,
            ["AGOSTO"] = 8,
            ["SEPTIEMBRE"] = 9,
            ["SETIEMBRE"] = 9,
            ["OCTUBRE"] = 10,
            ["NOVIEMBRE"] = 11,
            ["DICIEMBRE"] = 12
        };

        private static readonly Regex RxAmountAny = new(
            @"(?<!\w)\$?\s*[+-]?\s*(?:\d\s*){1,3}(?:\.\s*(?:\d\s*){3})*\s*,\s*\d\s*\d(?!\w)",
            RegexOptions.Compiled);

        // dd/mm   ó   dd/mm/yy   ó   dd/mm/yyyy  (tolerante a espacios)
        private static readonly Regex RxDateDdMmOptYear = new(
            @"(?<dd>\d{1,2})\s*/\s*(?<mm>\d{1,2})(?:\s*/\s*(?<yy>\d{2,4}))?",
            RegexOptions.Compiled);

        // ===== Nuevos tipos =====
        private sealed record CuentaCtx(string Account, string Currency);

        private sealed record LogicalLine(string Norm, string Orig);

        private sealed record TxRaw(string Line, decimal Amount, decimal Balance, DateTime Date, string Description);

        // === normalización quirúrgica para montos/fechas en una línea (solo para matching)
        private static string NormalizeForAmounts(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;
            s = NormalizeDashes(s);
            s = Regex.Replace(s, @"\s+", " ");
            s = Regex.Replace(s, @"(?<=\d)\s+(?=[\.,-])|(?<=[\.,-])\s+(?=\d)", "");
            s = Regex.Replace(s, @"\$\s+", "$");
            s = Regex.Replace(s, @"-\s+(?=[\d\$])", "-");
            return s.Trim();
        }

        // Evitar mezclar rótulos/totales en reflow
        private static bool LooksLikeHeaderOrTotals(string line)
        {
            var upper = RemoveDiacritics(line ?? string.Empty).ToUpperInvariant();
            string[] stop =
            {
                "SALDO ANTERIOR", "SALDO AL", "TOTAL MOVIMIENTOS",
                "IMPUESTO A LOS DEBITOS", "IMPUESTO A LOS DÉBITOS",
                "LEGALES", "AVISOS", "RESUMEN", "CONSOLIDADO",
                "TRANSFERENCIAS", "RECIBIDAS", "ENVIADAS",
                "TARJETAS DE DEBITO", "COMPRAS VISA DEBITO", "DETALLE", "FECHA TARJETA COMERCIO"
            };
            return stop.Any(upper.Contains);
        }

        private static string StripAsterisksAndNoise(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;
            // Quita bloques tipo "* * * *", "**", etc.
            s = Regex.Replace(s, @"(?:\*\s*){2,}", " ");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s;
        }

        private static readonly HashSet<string> OneLetterWhitelist = new(StringComparer.OrdinalIgnoreCase)
        {
            // Si hiciera falta permitir alguna letra suelta, agregar aquí.
        };

        private static readonly HashSet<string> LowerTokenWhitelist = new(StringComparer.OrdinalIgnoreCase)
        {
            // Palabras minúsculas que quieras conservar si alguna vez aplica.
        };

        // Regla Base para limpieza de descripciones
        private static string FixCommonOcrTokens(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;

            // separaciones conocidas
            s = Regex.Replace(s, @"\bIVATASAGENERALV?\b", "IVA TASA GENERAL", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bCOMITRANSFERENCIAR?\b", "COMI TRANSFERENCIA", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bCAPITALDOCUM\b", "CAPITAL DOCUM", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bINTERESESDOCUM\b", "INTERESES DOCUM", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bIMPUESTOSDOCUM\b", "IMPUESTOS DOCUM", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bOPERACIONENEFECTIVO\b", "OPERACION EN EFECTIVO", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bDEPOSITOAUTOSERVICIOPLUS\b", "DEPOSITO AUTOSERVICIO PLUS", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bPERC\s*\.\s*CABAING\s*\.\s*BRUTOS\b", "PERC CABA ING BRUTOS", RegexOptions.IgnoreCase);

            // pérdidas de primera letra
            s = Regex.Replace(s, @"(?<=^|\s)MP\s+LEY\b", "IMP LEY", RegexOptions.IgnoreCase); // "MP LEY" -> "IMP LEY"
            s = Regex.Replace(s, @"(?<=^|\s)EY(?=\s+(NRO|\d))", "LEY", RegexOptions.IgnoreCase); // "EY NRO" -> "LEY ..."

            // basura de cola
            s = Regex.Replace(s, @"\bBRUTOSA\b", "BRUTOS", RegexOptions.IgnoreCase);

            // puntos sueltos -> espacio
            s = Regex.Replace(s, @"\s*\.\s*", " ");

            // espacios
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        private static string FixExtraOcrTokens(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;

            // “IVAR . I .” => “IVA RI”
            s = Regex.Replace(s, @"\bIVAR?\s*\.\s*I\s*\.\b", "IVA RI", RegexOptions.IgnoreCase);

            // “LEY NRO 25 413” / “LEY NRO 25.413” => forzamos “25.413”
            s = Regex.Replace(s, @"\bLEY\s*NRO?\s*25\s*\.?\s*413\b", "LEY NRO 25.413", RegexOptions.IgnoreCase);

            // “SOBRECREDIT” o “SOBRECREDITV” => “SOBRE CREDITOS”
            s = Regex.Replace(s, @"\bSOBRECREDITV?\b", "SOBRE CREDITOS", RegexOptions.IgnoreCase);

            // “DEBITOCUOTA” => “DEBITO CUOTA”
            s = Regex.Replace(s, @"\bDEBITOCUOTA\b", "DEBITO CUOTA", RegexOptions.IgnoreCase);

            // “DB / CRPORPAGODESUELDOS” => “DB/CR POR PAGO DE SUELDOS”
            s = Regex.Replace(s, @"\bDB\s*/\s*CRPORPAGODESUELDOS\b", "DB/CR POR PAGO DE SUELDOS", RegexOptions.IgnoreCase);

            // “DEBITOPORPAGODEHABERESxxx” => “DEBITO POR PAGO DE HABERES”
            s = Regex.Replace(s, @"\bDEBITOPORPAGODEHABERES[A-Za-z]{1,3}\b", "DEBITO POR PAGO DE HABERES", RegexOptions.IgnoreCase);

            // “ACRED . PRESTAMO NRO :” variantes -> “ACRED PRESTAMO NRO ”
            s = Regex.Replace(s, @"\bACRED\s*\.\s*PRESTAMO\s*NRO\s*:?", "ACRED PRESTAMO NRO ", RegexOptions.IgnoreCase);

            // “TRANSF . CLIENTECTA . CAP” -> “TRANSF CLIENTE CTA CAP”
            s = Regex.Replace(s, @"\bTRANSF\s*\.\s*CLIENTECTA\s*\.\s*CAP\b", "TRANSF CLIENTE CTA CAP", RegexOptions.IgnoreCase);

            // “DEPOSITO AUTOSERVICIO PLUS” seguido de letra colgante => quitar
            s = Regex.Replace(s, @"\b(DEPOSITO AUTOSERVICIO PLUS)\s+[A-Za-z]\b", "$1", RegexOptions.IgnoreCase);

            // “COMI TRANSFERENCIA” / “IVA TASA GENERAL” truncados
            s = Regex.Replace(s, @"\bCOMI\s+TRANSFEREN\b", "COMI TRANSFERENCIA", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bIVA\s+TASA\s+GENE\b", "IVA TASA GENERAL", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bPERC\s+CABA\s+ING\s+BRU\b", "PERC CABA ING BRUTOS", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bOPERACION\s+EN\s+EFECTIVO\s*TARJE\b", "OPERACION EN EFECTIVO TARJETA", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bPAGO\s+TARJETA\s+VISA\s+EMPR\b", "PAGO TARJETA VISA EMPRESA", RegexOptions.IgnoreCase);

            // compactar espacios
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        // Quita tokens minúsculos sueltos (1–3 letras) cuando son ruido en descripciones mayormente en mayúsculas
        private static string RemoveStrayLowerTokens(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;
            var tokens = Regex.Split(s, @"(\s+)");
            int upperish = 0, lowerShort = 0;

            foreach (var t in tokens)
            {
                var w = t.Trim();
                if (string.IsNullOrEmpty(w)) continue;
                if (Regex.IsMatch(w, @"^[A-ZÁÉÍÓÚÑ0-9]{2,}$")) upperish++;
                if (Regex.IsMatch(w, @"^[a-záéíóúñ]{1,3}$")) lowerShort++;
            }

            if (upperish >= 2 && lowerShort >= 1)
            {
                for (int i = 0; i < tokens.Length; i++)
                {
                    var w = tokens[i];
                    if (Regex.IsMatch(w, @"^[a-záéíóúñ]{1,3}$") && !LowerTokenWhitelist.Contains(w))
                    {
                        tokens[i] = "";
                    }
                }
            }

            var cleaned = string.Concat(tokens);
            return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        }

        // Quita letras sueltas (con o sin punto) que no estén whitelisteadas – ahora con \p{L} para Unicode
        private static string DropLonelyLetters(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;

            var cleaned = Regex.Replace(s, @"\b(\p{L})\.?\b", m =>
            {
                var t = m.Groups[1].Value;
                return OneLetterWhitelist.Contains(t) ? t : " ";
            });

            return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        }

        // Limpia sufijos/prefijos “colgantes”: minúsculas o puntuación al final/inicio
        private static string TrimTrailingGarbage(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;

            // Quitar chatarra no alfanumérica final/inicial
            s = Regex.Replace(s, @"[^\p{L}\p{N}\s]+$", "");
            s = Regex.Replace(s, @"^[^\p{L}\p{N}\s]+", "");

            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        // Quita cualquier letra minúscula Unicode (a–z + acentos)
        private static string DropAllLowercaseLetters(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? string.Empty;
            // \p{Ll} = categoría Unicode "lowercase letter"
            s = Regex.Replace(s, @"\p{Ll}+", "");
            return Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        private static string CleanDesc(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // 1) sacá el prefijo de 3 dígitos (319/500/733, etc.)
            var a = Regex.Replace(s, @"^\s*\d{3}\s+", string.Empty);

            // 2) normalizá espacios y puntos raros tipo "DOCUM . DESCONT ."
            s = Regex.Replace(a, @"\s*\.\s*", ". ");  // espacio uniforme después del punto
            s = Regex.Replace(a, @"\s+", " ").Trim();

            return s;
        }


        private static string PostProcessDescription(string s)
        {
            s = FixCommonOcrTokens(s);
            s = FixExtraOcrTokens(s);
            s = DropAllLowercaseLetters(s);
            s = RemoveStrayLowerTokens(s);
            s = DropLonelyLetters(s);
            s = TrimTrailingGarbage(s);
            s = CleanDesc(s);
            return s;
        }


        // ===== Reflow: devuelve Norm (para regex) y Orig (dump intacto) =====
        private static IEnumerable<LogicalLine> ReflowLogicalLines(IEnumerable<string> rawLines)
        {
            var bufNorm = new StringBuilder();
            var bufOrig = new StringBuilder();

            foreach (var raw in rawLines)
            {
                var norm = NormalizeForAmounts(raw);
                var orig = raw ?? string.Empty;

                if (string.IsNullOrWhiteSpace(norm)) continue;
                if (LooksLikeHeaderOrTotals(norm))
                {
                    if (bufNorm.Length > 0)
                    {
                        yield return new LogicalLine(bufNorm.ToString(), bufOrig.ToString());
                        bufNorm.Clear();
                        bufOrig.Clear();
                    }
                    continue;
                }

                if (bufNorm.Length > 0) bufNorm.Append(' ');
                if (bufOrig.Length > 0) bufOrig.Append(' ');

                bufNorm.Append(norm);
                bufOrig.Append(orig);

                var cur = bufNorm.ToString();
                var mAmounts = RxAmountSpaced.Matches(cur);
                bool hasDate = RxDdMmAny.IsMatch(cur);

                if (mAmounts.Count >= 2 && hasDate)
                {
                    yield return new LogicalLine(bufNorm.ToString(), bufOrig.ToString());
                    bufNorm.Clear();
                    bufOrig.Clear();
                }
            }

            if (bufNorm.Length > 0)
                yield return new LogicalLine(bufNorm.ToString(), bufOrig.ToString());
        }

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

            var am = RxAmountSpaced.Matches(NormalizeForAmounts(line));
            if (am.Count == 0) return false;

            var tok = NormalizeAmountToken(am[^1].Value);
            return decimal.TryParse(tok, NumberStyles.Number | NumberStyles.AllowLeadingSign, EsAr, out opening);
        }

        private static bool TryParseClosingBalance(string line, int yearHint, out decimal closing, out DateTime? closingDate)
        {
            closing = 0m; closingDate = null;

            var compact = NormCompact(line);
            if (!compact.Contains("SALDOAL")) return false;

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

            var am = RxAmountSpaced.Matches(NormalizeForAmounts(line));
            if (am.Count == 0) return false;

            var tok = NormalizeAmountToken(am[^1].Value);
            return decimal.TryParse(tok, NumberStyles.Number | NumberStyles.AllowLeadingSign, EsAr, out closing);
        }

        // ===================== Helpers numéricos/fecha =====================

        private static string NormalizeDashes(string s) =>
            string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "[\u2010\u2011\u2012\u2013\u2014\u2015\u2212]", "-");

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
                return abs <= 50_000_000m; // subimos margen para OCR raros
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

        // Detectar año si la fecha viene como dd/mm/yy o dd/mm/yyyy en ese mismo punto
        private static int InferYearFromContext(string norm, Match ddmmMatch, int fallbackYear)
        {
            try
            {
                // Reintentar con el regex que incluye año, desde el mismo índice
                var m = RxDateDdMmOptYear.Match(norm, ddmmMatch.Index);
                if (m.Success && m.Index == ddmmMatch.Index)
                {
                    var yy = m.Groups["yy"]?.Value;
                    if (!string.IsNullOrEmpty(yy))
                    {
                        if (yy.Length == 2)
                        {
                            int y2 = int.Parse(yy);
                            return (y2 >= 80) ? 1900 + y2 : 2000 + y2;
                        }
                        if (yy.Length == 4 && int.TryParse(yy, out int y4))
                            return y4;
                    }
                }
            }
            catch { /* fallback */ }
            return fallbackYear;
        }

        // ===================== Sanitizado de descripciones (con regla de 3 espacios) =====================

        private static bool IsLetterAtom(string a) => a.Length == 1 && char.IsLetter(a[0]);
        private static bool IsDigitAtom(string a) => a.Length == 1 && char.IsDigit(a[0]);
        private static bool IsDotAtom(string a) => a == ".";

        // 3+ espacios separan palabras de alto nivel (del dump)
        private static IEnumerable<string> SplitDumpWords(string s) =>
            Regex.Split(s ?? string.Empty, @" {3,}").Where(x => !string.IsNullOrWhiteSpace(x));

        // Átomos letra/dígito/punto (ignorando espacios internos simples)
        private static List<string> ToAtoms(string segment)
        {
            var atoms = new List<string>();
            foreach (Match m in Regex.Matches(segment ?? string.Empty, @"\p{L}|\d|\."))
                atoms.Add(m.Value);
            return atoms;
        }

        // Reconstrucción dentro de cada segmento entre 3+ espacios
        private static List<string> RebuildSegmentWords(string segment)
        {
            var atoms = ToAtoms(segment);
            var result = new List<string>();
            int i = 0;

            while (i < atoms.Count)
            {
                // Números (con puntos internos) -> un token
                if (IsDigitAtom(atoms[i]))
                {
                    var sb = new StringBuilder();
                    while (i < atoms.Count && (IsDigitAtom(atoms[i]) || IsDotAtom(atoms[i])))
                    {
                        sb.Append(atoms[i]);
                        i++;
                    }
                    result.Add(sb.ToString());
                    continue;
                }

                // Letras: detectar sigla L . L . L
                if (IsLetterAtom(atoms[i]))
                {
                    int j = i;
                    var letters = new StringBuilder();
                    bool sawAcronymPattern = false;

                    while (j < atoms.Count)
                    {
                        if (!IsLetterAtom(atoms[j])) break;
                        letters.Append(atoms[j]);
                        j++;

                        if (j + 1 < atoms.Count && IsDotAtom(atoms[j]) && IsLetterAtom(atoms[j + 1]))
                        {
                            sawAcronymPattern = true;
                            j += 1; // saltar el punto
                            continue;
                        }
                        break;
                    }

                    if (sawAcronymPattern)
                    {
                        if (j < atoms.Count && IsDotAtom(atoms[j])) j++; // posible punto final
                        result.Add(letters.ToString());
                        i = j;
                        continue;
                    }

                    // No es sigla: juntar solo letras contiguas → palabra
                    var word = new StringBuilder();
                    while (i < atoms.Count && IsLetterAtom(atoms[i]))
                    {
                        word.Append(atoms[i]);
                        i++;
                    }
                    if (i < atoms.Count && IsDotAtom(atoms[i])) i++; // punto separador
                    result.Add(word.ToString());
                    continue;
                }

                // Puntos sueltos = separador
                if (IsDotAtom(atoms[i])) { i++; continue; }

                i++;
            }

            return result;
        }

        // Regla principal para Description basada en espaciado del dump
        private static string SanitizeDescription(string rawDesc)
        {
            if (string.IsNullOrWhiteSpace(rawDesc)) return rawDesc ?? string.Empty;

            var words = new List<string>();
            foreach (var segment in SplitDumpWords(rawDesc))
            {
                var rebuilt = RebuildSegmentWords(segment);
                if (rebuilt.Count > 0) words.AddRange(rebuilt);
            }
            return string.Join(" ", words).Trim();
        }

        // Para mostrar línea original legible (sin inventar palabras)
        private static string SanitizeOriginalForDisplay(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return line ?? string.Empty;
            var s = NormalizeForAmounts(line);
            s = CondenseSpacedLetters(s);
            s = CompactDigits(s);
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s;
        }

        // ===== Adaptador: mantiene firma antigua y delega al overload nuevo =====
        private static List<TxRaw> ParseTransactionsByHardRules(IEnumerable<string> rawLines, int yearHint, string currency) =>
            ParseTransactionsByHardRules(ReflowLogicalLines(rawLines), yearHint, currency);

        // ===== Nueva versión: usa Norm para detectar y Orig para cortar descripción =====
        private static List<TxRaw> ParseTransactionsByHardRules(IEnumerable<LogicalLine> logicalLines, int yearHint, string currency)
        {
            var result = new List<TxRaw>();

            foreach (var ll in logicalLines)
            {
                var rawNorm = ll.Norm;
                var rawOrig = ll.Orig;

                if (string.IsNullOrWhiteSpace(rawNorm)) continue;
                if (IsDecor(rawNorm)) continue;

                var amounts = RxAmountSpaced.Matches(rawNorm);
                if (amounts.Count < 2) continue;

                var movMatch = amounts[^2];
                var balMatch = amounts[^1];

                int idxMov = movMatch.Index;
                int idxBal = balMatch.Index;
                if (!(idxMov >= 0 && idxBal > idxMov)) continue;

                var dateMatchNorm = FindFirstDdMmAfter(rawNorm, idxBal);
                if (dateMatchNorm == null) continue;

                var movTok = NormalizeAmountToken(movMatch.Value);
                var balTok = NormalizeAmountToken(balMatch.Value);

                var dayTok = NormalizeDdMm(dateMatchNorm.Groups["dd"].Value);
                var monTok = NormalizeDdMm(dateMatchNorm.Groups["mm"].Value);

                if (!TryParseLatAm(movTok, out var mov)) continue;
                if (!TryParseLatAm(balTok, out var bal)) continue;
                if (!PassBalanceLimit(currency, bal)) continue;

                if (!int.TryParse(dayTok, out var d)) continue;
                if (!int.TryParse(monTok, out var mth)) continue;

                // Año: intentar tomarlo del mismo match si está en la línea; si no, usar hint externo
                int y = InferYearFromContext(rawNorm, dateMatchNorm, yearHint);
                if (!SafeDate(d, mth, y, out var date)) continue;

                // ==== Cortar la cola de descripción sobre el ORIGINAL (sin colapsar espacios) ====
                var dateMatchOrig = RxDdMmAny.Match(rawOrig);
                if (!dateMatchOrig.Success) continue;

                int idxDateOrig = dateMatchOrig.Index + dateMatchOrig.Length;
                string tailOrig = rawOrig.Substring(idxDateOrig);

                // Origen (opcional) inmediatamente después de la fecha (en el original) — más permisivo
                var orgOrig = RxOriginAfterDate.Match(tailOrig);
                if (orgOrig.Success)
                {
                    tailOrig = tailOrig.Substring(orgOrig.Length);
                }

                var desc = SanitizeDescription(tailOrig);

                result.Add(new TxRaw(rawOrig, mov, bal, date, desc));
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

            // 2) Detectar TODAS las cuentas
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

            var dedupByAccount = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // 3) Recorrer documento por bloques
            CuentaCtx? lastCtx = null;
            DateTime? globalEndByClosingLine = null;

            for (int i = 0; i < parts.Count; i++)
            {
                var ln = parts[i]?.Trim() ?? string.Empty;

                bool isTitle = IsMovTitle(ln);
                bool isHeader = ContainsHeaderMovs(ln);
                if (!isTitle && !isHeader) continue;

                int headerIdx = -1;
                int usedAccountIdx = -1;
                CuentaCtx? ctx = null;

                if (isTitle)
                {
                    for (int fwd = 1; fwd <= 12 && i + fwd < parts.Count; fwd++)
                    {
                        var maybe = TryParseAccountLine(parts[i + fwd]);
                        if (maybe != null) { ctx = maybe; usedAccountIdx = i + fwd; break; }
                        if (parts[i + fwd].StartsWith("<<PAGE:", StringComparison.Ordinal)) break;
                    }
                    for (int fwd = 1; fwd <= 20 && i + fwd < parts.Count; fwd++)
                    {
                        if (ContainsHeaderMovs(parts[i + fwd])) { headerIdx = i + fwd; break; }
                        if (parts[i + fwd].StartsWith("<<PAGE:", StringComparison.Ordinal)) break;
                    }
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
                    headerIdx = i;

                    for (int back = 1; back <= 12 && i - back >= 0; back++)
                    {
                        var maybe = TryParseAccountLine(parts[i - back]);
                        if (maybe != null) { ctx = maybe; usedAccountIdx = i - back; break; }
                        if (IsMovTitle(parts[i - back])) break;
                    }
                    if (ctx == null)
                    {
                        for (int fwd = 1; fwd <= 12 && i + fwd < parts.Count; fwd++)
                        {
                            var maybe = TryParseAccountLine(parts[i + fwd]);
                            if (maybe != null) { ctx = maybe; usedAccountIdx = i + fwd; break; }
                            if (IsMovTitle(parts[i + fwd]) || ContainsHeaderMovs(parts[i + fwd])) break;
                        }
                    }
                    if (ctx == null && lastCtx != null) ctx = lastCtx;
                }

                if (ctx == null || headerIdx < 0) continue;

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

                accObj.Transactions ??= new List<Transaction>();
                var txs = accObj.Transactions;

                // de-duplicado set por cuenta
                if (!dedupByAccount.TryGetValue(accKey, out var seen))
                {
                    seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var t in txs)
                    {
                        var k0 = BuildTxKey(t.Date, t.Amount, t.Balance, t.OriginalDescription ?? string.Empty);
                        seen.Add(k0);
                    }
                    dedupByAccount[accKey] = seen;
                }

                // Capturar bloque desde debajo de la cabecera
                var block = new List<string>();
                bool skippedCtxAccountOnce = false;
                for (int j = headerIdx + 1; j < parts.Count; j++)
                {
                    var l2 = parts[j];

                    if (IsMovTitle(l2) || ContainsHeaderMovs(l2) || l2.StartsWith("<<PAGE:", StringComparison.Ordinal))
                        break;

                    var accLine = TryParseAccountLine(l2);
                    if (accLine != null)
                    {
                        if (!skippedCtxAccountOnce && usedAccountIdx == j)
                        {
                            skippedCtxAccountOnce = true;
                            continue;
                        }
                        break;
                    }

                    if (IsDecor(l2)) continue;
                    block.Add(l2);
                }
                if (block.Count == 0) continue;

                // SALDO ANTERIOR
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

                // SALDO AL ...
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

                // ------ Año de referencia para este bloque ------
                int yearHintForBlock = (globalEndByClosingLine?.Year) ?? YearHint();

                // Parsear transacciones
                var raws = ParseTransactionsByHardRules(block, yearHintForBlock, ctx.Currency);

                // Reconciliación contable para IsSuspicious
                decimal? prevBalance = null;

                bool haveSeed =
                    openingInThisBlock.HasValue ||
                    (txs != null && txs.Count > 0) ||
                    (accObj.OpeningBalance.HasValue && accObj.OpeningBalance.Value != 0m);

                if (openingInThisBlock.HasValue) prevBalance = openingInThisBlock.Value;
                else if (txs != null && txs.Count > 0) prevBalance = txs[txs.Count - 1].Balance;
                else if (accObj.OpeningBalance.HasValue && accObj.OpeningBalance.Value != 0m) prevBalance = accObj.OpeningBalance.Value;

                // Cero no confiable: no reconciliar la primera
                if (!openingInThisBlock.HasValue &&
                    (txs == null || txs.Count == 0) &&
                    accObj.OpeningBalance.HasValue &&
                    accObj.OpeningBalance.Value == 0m)
                {
                    prevBalance = null;
                }

                DateTime? lastSeenDate = (txs != null && txs.Count > 0)
                    ? txs[txs.Count - 1].Date
                    : (DateTime?)null;

                foreach (var tx in raws)
                {
                    bool isSusp = false;
                    bool isNewDay = lastSeenDate == null || tx.Date.Date > lastSeenDate.Value.Date;

                    if (haveSeed && prevBalance.HasValue)
                    {
                        bool ok = Reconciles(prevBalance.Value, tx.Amount, tx.Balance);

                        if (!ok && isNewDay)
                        {
                            isSusp = false;                 // reinicio suave al cambiar de día
                            prevBalance = tx.Balance;
                            haveSeed = true;
                        }
                        else
                        {
                            isSusp = !ok;
                            prevBalance = tx.Balance;
                        }
                    }
                    else
                    {
                        prevBalance = tx.Balance;
                        haveSeed = true;
                    }

                    lastSeenDate = tx.Date;

                    // De-duplicado por cuenta
                    var k = BuildTxKey(tx.Date, tx.Amount, tx.Balance, tx.Line);
                    if (seen.Contains(k)) continue;
                    seen.Add(k);

                    txs!.Add(new Transaction
                    {
                        Date = tx.Date,
                        Description = PostProcessDescription(tx.Description), // doble pasada robusta
                        OriginalDescription = SanitizeOriginalForDisplay(tx.Line),
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
                var txs2 = a.Transactions;
                if (txs2 != null && txs2.Count > 0)
                {
                    var min = txs2.Min(t => t.Date);
                    var max = txs2.Max(t => t.Date);
                    if (pStart == null || min < pStart) pStart = min;
                    if (pEnd == null || max > pEnd) pEnd = max;

                    if (a.ClosingBalance == null)
                    {
                        var last = txs2[txs2.Count - 1];
                        a.ClosingBalance = last.Balance;
                    }
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

        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress)
        {
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("start", 0, 3));
            var r = Parse(text);
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("parsed", 2, 3));
            progress?.Invoke(new IBankStatementParser.ProgressUpdate("done", 3, 3));
            return r;
        }

        // ===================== helpers privados (anti-duplicado) =====================
        private static string BuildTxKey(DateTime date, decimal amount, decimal balance, string? originalLine)
        {
            var norm = NormCompact(originalLine ?? string.Empty);
            return $"{date:yyyy-MM-dd}|{amount.ToString(CultureInfo.InvariantCulture)}|{balance.ToString(CultureInfo.InvariantCulture)}|{norm}";
        }
    }
}
