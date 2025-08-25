using System.Threading;
using System.Threading.Tasks;
using AccountCore.DTO.Parser.Parameters;
using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public interface ICategorizationService
    {
        Task ApplyAsync(ParseResult result, string bank, string userId, CancellationToken ct = default);

        Task<UserCategoryRule> LearnAsync(LearnRuleRequest req, CancellationToken ct = default);
    }
}
