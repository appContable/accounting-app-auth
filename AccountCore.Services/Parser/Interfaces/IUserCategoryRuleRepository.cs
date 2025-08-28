using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public interface IUserCategoryRuleRepository
    {
        Task UpsertAsync(UserCategoryRule rule, CancellationToken ct = default);
        Task EnsureIndexesAsync(CancellationToken ct = default);
        Task<IReadOnlyList<UserCategoryRule>> GetByUserAndBankAsync(string userId, string bank, CancellationToken ct = default);
    }
}
