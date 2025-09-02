using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public interface IBankCategoryRuleRepository
    {
        Task<BankCategoryRule?> FindByBankAndPatternAsync(string bank, string pattern, CancellationToken ct = default);
        Task InsertAsync(BankCategoryRule rule, CancellationToken ct = default);
        Task<IReadOnlyList<BankCategoryRule>> GetByBankAsync(string bank, CancellationToken ct = default);
    }
}
