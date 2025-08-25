using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParserDAL.Models;

namespace ParserServices.Interfaces
{
    public interface IBankCategoryRuleRepository
    {
        Task<BankCategoryRule?> FindByBankAndPatternAsync(string bank, string pattern, CancellationToken ct = default);
        Task InsertAsync(BankCategoryRule rule, CancellationToken ct = default);
        Task<IReadOnlyList<BankCategoryRule>> GetByBankAsync(string bank, CancellationToken ct = default);
    }
}
