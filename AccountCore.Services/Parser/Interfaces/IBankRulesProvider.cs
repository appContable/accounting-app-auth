using System.Collections.Generic;
using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public record BankRule(string Pattern, RulePatternType PatternType, string Category, string? Subcategory, int Priority);

    public interface IBankRulesProvider
    {
        IReadOnlyList<BankRule> GetForBank(string bank);
    }
}
