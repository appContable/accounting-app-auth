namespace AccountCore.DAL.Parser.Models
{
    public enum RulePatternType { Contains, StartsWith, EndsWith, Equals, Regex }
    public enum RuleOrigin { System, Manual }
    public enum CategorySource { None, BankRule, UserRule}
}
