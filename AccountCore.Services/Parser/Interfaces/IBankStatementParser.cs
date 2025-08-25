using System;
using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public interface IBankStatementParser
    {
        // Para reportar progreso (opcional)
        public record ProgressUpdate(string Stage, int Current, int Total);

        ParseResult Parse(string text, Action<ProgressUpdate>? progress = null);
    }
}
