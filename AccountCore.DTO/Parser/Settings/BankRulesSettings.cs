using AccountCore.DAL.Parser.Models;

namespace AccountCore.DTO.Parser.Settings
{
    public class BankRulesSettings
    {
        public Dictionary<string, List<BankRuleItem>> Banks { get; set; } = new();
    }

    public class BankRuleItem
    {
        public string Pattern { get; set; } = string.Empty;
        public RulePatternType PatternType { get; set; } = RulePatternType.Contains;
        public string Category { get; set; } = string.Empty;
        public string? Subcategory { get; set; }
        public int Priority { get; set; } = 0;
    }
}
