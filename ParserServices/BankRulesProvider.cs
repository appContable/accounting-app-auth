using Microsoft.Extensions.Options;
using ParserDAL.Models;
using ParserDTO.Settings;
using ParserServices.Interfaces;

namespace ParserServices
{
    public class BankRulesProvider : IBankRulesProvider
    {
        private readonly BankRulesSettings _settings;
        public BankRulesProvider(IOptions<BankRulesSettings> options) => _settings = options.Value;

        public IReadOnlyList<BankRule> GetForBank(string bank)
        {
            if (!_settings.Banks.TryGetValue(bank, out var list) || list is null)
                return Array.Empty<BankRule>();

            return list
                .Select(r => new BankRule(
                    r.Pattern,
                    r.PatternType,
                    r.Category,
                    r.Subcategory,
                    r.Priority))
                .OrderBy(r => r.Priority)
                .ToList();
        }

        private static RulePatternType ToEnum(string s)
            => Enum.TryParse<RulePatternType>(s, true, out var e) ? e : RulePatternType.Contains;
    }
}
