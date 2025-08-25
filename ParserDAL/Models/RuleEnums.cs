namespace ParserDAL.Models
{
    public enum RulePatternType { Contains, StartsWith, EndsWith, Equals, Regex }
    public enum RuleOrigin { System, Manual, Learned }
    public enum CategorySource { None, BankRule, UserRule, UserLearned }
}
