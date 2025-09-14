using System.Text.RegularExpressions;

namespace AccountCore.Services.Parser.Parsers
{
    internal static class DescriptionBuilder
    {
        // Dinero ESTRICTO (para reconocer montos válidos en un texto)
        private static readonly Regex RxStrictMoneyToken = new(
            @"(?<![\p{L}\p{Nd}])-?\d{1,3}(?:\.\d{3})*,\d{2}-?(?![\p{L}\p{Nd}])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Fragmentos numéricos rotos SIN coma (ej. "-81", "-1.1") -> basura en extras
        private static readonly Regex RxBrokenMoneyFragment = new(
            @"(?<![\p{L}\p{Nd}])-?\d{1,3}(?:\.\d{3})*(?!,\d)(?![\p{L}\p{Nd}])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        // Importes crudos EXACTOS del PDF (miles con '.' o espacio, decimales ',', signo '-' prefijo/sufijo)
        private static readonly Regex RxMoneyRaw = new(
            @"-?\d{1,3}(?:[ \.]\d{3})*,\d{2}-?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Encabezados/ruido obvio
        private static readonly Regex RxHeaderCols = new(
            @"\b(Fecha|Descripci[oó]n|Origen|Cr[eé]dito|D[eé]bito|Saldo)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 1) Reemplazá RxPageNoise por uno más robusto
        private static readonly Regex RxPageNoise = new Regex(
            @"<<PAGE:|^\s*P[aá]gina\s+\d+(?:\s+\d+)?\s*/\s*\d+\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxBankBoilerplate = new Regex(
            @"Resumen de|Cuenta Corriente en Pesos|CBU|Dispon[eé]s de 30 d[ií]as|cr[eé]dito fiscal|" +
            @"Tasa Extraordinaria|Promedio\s+\d{6}|Saldos\s*Deudores|Datos de la cuenta|Per[ií]odo de mov",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RxPurePageFraction = new Regex(
            @"^\s*\d+(?:\s+\d+)?\s*/\s*\d+\s*$",
            RegexOptions.Compiled);


        // Identificadores útiles
        private static readonly Regex RxCUIT = new(
            @"\b\d{2}-\d{8}-\d\b|\b\d{11}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex RxCBU = new(
            @"\b\d{22}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex RxAlphaNumCode = new(
            @"\b[A-Z]{1,6}[A-Z0-9]{5,}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // NUEVO: marcador de página embebido (no necesariamente al inicio/fin)
        private static readonly Regex RxInlinePageMarker = new(
            @"\bP[aá]gina\s+\d+(?:\s+\d+)?\s*/\s*\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Splitter de líneas: soporta saltos y separadores @@@ del OCR
        private static readonly Regex RxLineSplit = new(
            @"(?:\r?\n|@@@)+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Palabras de “Origen/detalle” que conviene mantener
        private static readonly HashSet<string> KeepKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "HABERES","ACRED.HABERES","PROVEEDORES","DEUDORES","AFIP","HONORARIOS",
            "REG.RECAU.SIRCREB","FIMA PREMIUM CLASE A","BANCO SANTANDER RIO",
            "NUEVO BANCO DE SANTA","LAMERCANTIL AND","MERCADO LIBRE SRL"
        };

        private static readonly Regex RxDateAtStart = new(
            @"^\s*\d{2}/\d{2}/\d{2}\s*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Normaliza solo para detectar importes con OCR ruidoso (no se usa para mostrar)
        private static string TightenForAmounts(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var t = Regex.Replace(s, @"(?<=\d)\s+(?=[\.,-])", ""); // 1 _ .  -> 1.
            t = Regex.Replace(t, @"(?<=[\.,-])\s+(?=\d)", "");     // . _ 500 -> .500
            return t;
        }

        private static string Tight(string s) =>
            Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();

        private static string DropLonelyMinusOne(string s) =>
            Regex.Replace(s ?? string.Empty, @"\s?-1\s?$", "").Trim();

        private static bool LooksLikeOnlyNoiseNumbers(string s) =>
            Regex.IsMatch(s ?? string.Empty, @"^[\d\.\,\-\s]+$");

        private static readonly Regex RxLongDigits = new(@"\b\d{10,}\b", RegexOptions.Compiled); // CBU, refs, tarjetas, ceros largos
        private static readonly Regex RxCUITLine = new(@"^\s*\d{2}-\d{8}-\d\s*$", RegexOptions.Compiled);
        private static readonly Regex RxCBULine = new(@"^\s*\d{22}\s*$", RegexOptions.Compiled);
        private static readonly Regex RxZerosLong = new(@"0{6,}", RegexOptions.Compiled); // tiras de ceros


        private static bool IsUsefulLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;

            // Boilerplate/columnas/ruido
            if (RxPageNoise.IsMatch(line)) return false;
            if (RxBankBoilerplate.IsMatch(line)) return false;
            if (RxPurePageFraction.IsMatch(line)) return false;
            if (RxHeaderCols.IsMatch(line)) return false;
            if (RxInlinePageMarker.IsMatch(line)) return false;

            // Filtrar IDs/líneas numéricas
            var t = line.Trim();
            if (RxCUITLine.IsMatch(t) || RxCBULine.IsMatch(t)) return false;
            if (RxLongDigits.IsMatch(t)) return false;
            if (RxZerosLong.IsMatch(t)) return false;
            if (Regex.IsMatch(t, @"^[\d\.\,\-\s]+$")) return false;

            // Si no hay letras, no sirve
            if (!Regex.IsMatch(t, @"[A-Za-zÁÉÍÓÚÑáéíóúñ]")) return false;

            // Descarta genéricos vacíos de origen
            if (t.Equals("PROPIA", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Equals("VARIOS", StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Equals("CUENTA ORIGEN", StringComparison.OrdinalIgnoreCase)) return false;

            // Si llegó hasta acá, vale la pena
            return true;
        }


        // --- Helpers per-line (clave para no “comernos” los extras) ---

        // Recorta una línea SOLO hasta el primer importe que esté en ESA línea
        private static string CutLineBeforeAmount(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return string.Empty;

            var lnNorm = TightenForAmounts(line);
            var m = RxMoneyRaw.Match(lnNorm);
            var pure = m.Success ? lnNorm[..m.Index] : lnNorm;

            pure = RxDateAtStart.Replace(pure, ""); // sacar fecha si quedó
            pure = Tight(pure);
            pure = DropLonelyMinusOne(pure);

            return pure;
        }

        private static IEnumerable<string> EnumerateUsefulLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) yield break;

            foreach (var raw in RxLineSplit.Split(text))
            {
                var l = CutLineBeforeAmount(raw);
                l = CleanBrokenMoneyFragments(l); 
                if (IsUsefulLine(l))
                    yield return l;
            }
        }

        private static string CleanBrokenMoneyFragments(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // si la línea tiene dígitos/coma pero NO tiene un monto estricto, borramos fragmentos tipo "-81"
            if (s.IndexOf(',') >= 0 && !RxStrictMoneyToken.IsMatch(s))
                s = RxBrokenMoneyFragment.Replace(s, "").Trim();

            // aunque no haya coma, removemos fragmentos sueltos que no sean montos estrictos
            s = RxBrokenMoneyFragment.Replace(s, "").Trim();

            // limpiar dobles espacios/puntuación suelta que haya quedado
            s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', '·', '—', '-', ')', '(');

            return s;
        }
        // -------- API pública --------

        /// <summary>
        /// Título desde detect (normalizado del parser) + extras desde el bloque crudo (línea a línea).
        /// </summary>
        public static string BuildSmart(string detectText, string blockDesc)
        {
            var title = TitleFromDetect(detectText);

            var extras = EnumerateUsefulLines(blockDesc)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Where(l => string.IsNullOrWhiteSpace(title) ||
                            !l.Equals(title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(title) && extras.Count == 0)
                return string.Empty;

            if (extras.Count == 0)
                return title;

            return $"{title} | {string.Join(" · ", extras)}";
        }

        /// <summary>
        /// Fallback simple con el bloque (línea a línea). No depende de detect.
        /// </summary>
        public static string Build(string blockDesc)
        {
            var parts = EnumerateUsefulLines(blockDesc).ToList();
            if (parts.Count == 0) return string.Empty;

            var baseTitle = parts[0];
            var extras = parts.Skip(1)
                              .Where(p => !p.Equals(baseTitle, StringComparison.OrdinalIgnoreCase))
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();

            return extras.Count == 0 ? baseTitle : $"{baseTitle} | {string.Join(" · ", extras)}";
        }

        // -------- Implementación del título desde detect --------

        private static string TitleFromDetect(string detectText)
        {
            if (string.IsNullOrWhiteSpace(detectText)) return string.Empty;

            // Tomo SOLO la primera línea de detect
            var firstLine = (detectText.Split('\n').FirstOrDefault() ?? detectText);
            firstLine = CutLineBeforeAmount(firstLine);

            // Quitar posible '$' colgando
            firstLine = Regex.Replace(firstLine, @"\s*\$\s*$", "");

            return Tight(firstLine);
        }
    }
}
