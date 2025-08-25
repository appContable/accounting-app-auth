using System;
using DAL = AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public interface IBankStatementParser
    {
        // Para reportar progreso (opcional)
        public record ProgressUpdate(string Stage, int Current, int Total);

        // ¡Clave!: que el retorno sea SIEMPRE el del DAL
        DAL.ParseResult Parse(string text, Action<ProgressUpdate>? progress = null);
    }
}
