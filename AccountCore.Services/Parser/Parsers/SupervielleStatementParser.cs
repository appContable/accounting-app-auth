using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using AccountCore.DAL.Parser.Models;                  // BankStatement, AccountStatement, Transaction
using AccountCore.Services.Parser.Interfaces;         // IBankStatementParser

namespace AccountCore.Services.Parser.Parsers
{
    /// <summary>
    /// Parser “RAW-first” para Banco Supervielle.
    /// Versión inicial: NO parsea cuentas ni movimientos; solo devuelve el RAW completo en warnings.
    /// Deja definidas las fases clave y los hooks de diagnóstico/progreso para ir sumando lógica por etapas.
    /// </summary>
    public class SupervielleStatementParser : IBankStatementParser
    {
        // ===== Config de diagnóstico (podés bajar el ruido desactivando RAW_FULL) =====
        private static readonly bool DIAGNOSTIC   = true;
        private static readonly bool RAW_FULL     = true;   // true: loguea el RAW completo, chunked
        private const int RAW_CHUNK_SIZE = 1600;            // tamaño de chunk para [raw-full #n]
        private const int RAW_MAX_CHUNKS = 999;             // sin límite práctico al iniciar

        // Marcadores que vienen del PdfParserService
        // (no son obligatorios, pero nos sirven cuando luego partamos por páginas/filas)
        private const string LINE_DELIM = "@@@";
        private const string PAGE_FMT   = "<<PAGE:{0}>>>";

        private Action<IBankStatementParser.ProgressUpdate>? _progress;
        private void Report(string stage, int current, int total)
            => _progress?.Invoke(new IBankStatementParser.ProgressUpdate(stage, current, total));

        // ========== Helpers mínimos ==========
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

        // Normalización “generalista” (no hace suposiciones de Galicia)
        private static string NormalizeWhole(string t)
        {
            if (string.IsNullOrEmpty(t)) return string.Empty;
            // 1) Unificar saltos
            t = t.Replace("\r\n", "\n").Replace('\r', '\n');

            // 2) Colapsar espacios anómalos (sin tocar los delimitadores del servicio)
            t = Regex.Replace(t, @"[ \t]+", " ");

            // 3) Acomodar fechas “rotas” típicas de OCR (sin asumir que son anchors)
            t = Regex.Replace(t, @"(?<!\d)(\d)\s+(?=\1/\d{2}/\d{2})", "$1"); // 1 1/04/23 -> 11/04/23
            t = Regex.Replace(t, @"(?<![\r\n])(?<!^)(?<!\d)(\d{1,2}/\d{1,2}/\d{2})", "\n$1");

            return t;
        }

        // Preprocesamiento neutro (espacio para reglas de limpieza específicas de Supervielle)
        private static string Preprocess(string t)
        {
            if (string.IsNullOrEmpty(t)) return string.Empty;
            // Ejemplo: quitar doc-id o banners si los detectamos luego (a definir con evidencia)
            return t;
        }

        // Multi-cuenta: en Supervielle no asumimos “portada única”. Este stub solo esqueleto.
        private static List<(string accountKey, string sliceRaw)> SplitByAccounts(string t)
        {
            // FUTURO: detectar “anclas” de cuenta (e.g., “NUMERO DE CUENTA …”, “CBU …”, headers seccionales).
            // Por ahora devolvemos todo como un único bloque, sin identificar cuentas.
            return new List<(string, string)>{ ("(unknown)", t) };
        }

        // Región de movimientos de cada cuenta (por ahora, passthrough)
        private static string ExtractMovementsRegion(string accountSliceRaw)
        {
            // FUTURO: cortar encabezados/pies por secciones y quedarnos con la tabla real.
            return accountSliceRaw;
        }

        // Cuando empecemos a parsear, acá irá el bucle de líneas, fechas, montos, saldo, etc.
        private static List<Transaction> ParseTransactionsLines(string regionRaw)
        {
            // FUTURO: implementar line reader robusto (no asumir fecha como primer token al inicio).
            // Esta versión inicial devuelve vacío.
            return new List<Transaction>();
        }

        // Seteo del período en base a transacciones (cuando existan)
        private static void BuildStatementHeaderFromTransactions(ParseResult result)
        {
            var all = result.Statement.Accounts?.SelectMany(a => a.Transactions) ?? Enumerable.Empty<Transaction>();
            if (!all.Any()) return;

            result.Statement.PeriodStart = all.Min(t => t.Date);
            result.Statement.PeriodEnd   = all.Max(t => t.Date);
        }

        // ==========================
        //      MÉTODO PRINCIPAL
        // ==========================
        public ParseResult Parse(string text, Action<IBankStatementParser.ProgressUpdate>? progress = null)
        {
            _progress = progress;

            var result = new ParseResult
            {
                Statement = new BankStatement
                {
                    Bank = "Banco Supervielle",
                    Accounts = new List<AccountStatement>() // Por ahora vacío (RAW-first)
                },
                Warnings = new List<string>()
            };

            // Fase 0: RAW completo (observabilidad primero)
            Report("RAW", 1, 6);
            if (DIAGNOSTIC)
                EmitRawFull(result, text ?? string.Empty);

            // Fase 1: Normalizar (neutro, sin asumir anchors de Galicia)
            Report("Normalizando", 2, 6);
            var normalized = NormalizeWhole(text ?? string.Empty);

            // Fase 2: Preprocesar (espacio para limpiar banners/ids específicos si encontramos evidencia)
            Report("Preprocesando", 3, 6);
            var pre = Preprocess(normalized);

            // Fase 3: Detectar cuentas (multi-cuenta)
            Report("Detectando cuentas", 4, 6);
            var accounts = SplitByAccounts(pre);

            // Fase 4: (stub) movimientos por cuenta — por ahora NO parseamos; solo definimos el esqueleto
            Report("Stub movimientos", 5, 6);
            foreach (var (accountKey, sliceRaw) in accounts)
            {
                // Región de movimientos (por ahora passthrough)
                var region = ExtractMovementsRegion(sliceRaw);

                // Placeholder de cuenta vacía; cuando haya reglas, creamos transacciones.
                var account = new AccountStatement
                {
                    AccountNumber = accountKey,  // En el futuro: “22-0458… (ARS)” o similar
                    Transactions  = new List<Transaction>(),
                    OpeningBalance = null,
                    ClosingBalance = null,
                    Currency = "ARS"
                };

                result.Statement.Accounts.Add(account);
            }

            // Fase 5: Header/período (se poblará cuando haya transacciones)
            Report("Header período", 6, 6);
            BuildStatementHeaderFromTransactions(result);

            // Nota para el dev: recordatorio de qué hace esta versión
            if (DIAGNOSTIC)
                result.Warnings.Add("[note] Supervielle RAW-only: no se parsearon cuentas/movimientos todavía");

            return result;
        }
    }
}
