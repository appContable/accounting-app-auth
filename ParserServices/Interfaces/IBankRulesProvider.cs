using System.Collections.Generic;
using ParserDAL.Models;

namespace ParserServices.Interfaces
{
    public record BankRule(string Pattern, RulePatternType PatternType, string Category, string? Subcategory, int Priority);

    public interface IBankRulesProvider
    {
        IReadOnlyList<BankRule> GetForBank(string bank);
    }
}
