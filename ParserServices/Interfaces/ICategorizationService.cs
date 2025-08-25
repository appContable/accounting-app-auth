using System.Threading;
using System.Threading.Tasks;
using ParserDTO.Parameters;
using DAL = ParserDAL.Models;

namespace ParserServices.Interfaces
{
    public interface ICategorizationService
    {
        // ParseResult del DAL
        Task ApplyAsync(DAL.ParseResult result, string bank, string userId, CancellationToken ct = default);

        // Devuelve la UserCategoryRule del DAL
        Task<DAL.UserCategoryRule> LearnAsync(LearnRuleRequest req, CancellationToken ct = default);
    }
}
