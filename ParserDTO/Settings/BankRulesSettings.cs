using System.Collections.Generic;
using ParserDAL.Models;

namespace ParserDTO.Settings
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
