using System;
using ParserDAL.Models; // RulePatternType

namespace ParserDTO
{
    public class UpsertUserRuleRequest
    {
        public Guid? Id { get; set; }                  // null para crear, guid para actualizar
        public string UserId { get; set; } = default!;
        public string Bank { get; set; } = default!;
        public string Pattern { get; set; } = default!;
        public RulePatternType PatternType { get; set; } = RulePatternType.Contains;
        public string Category { get; set; } = default!;
        public string? Subcategory { get; set; }
        public int? Priority { get; set; }             // si null => 100
    }
}
