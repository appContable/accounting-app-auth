using System.IO;
using System.Threading.Tasks;

// Alias explícito al modelo del DAL para evitar ambigüedad
using DAL = ParserDAL.Models;

namespace ParserServices.Interfaces
{
    public interface IPdfParsingService
    {
        // Firma canónica (sin CancellationToken) que usan tus controladores/servicios
        Task<DAL.ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId);
    }
}
