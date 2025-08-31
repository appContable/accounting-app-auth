using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public interface IPdfParsingService
    {
        Task<ParseResult?> ParseAsync(Stream pdfStream, string bank, string userId);
    }
}
