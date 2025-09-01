using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AccountCore.DAL.Parser.Models
{
    public class BankCategoryRule
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public string Bank { get; set; } = null!;

        public string Pattern { get; set; } = null!;

        [BsonRepresentation(BsonType.String)]
        public RulePatternType PatternType { get; set; } = RulePatternType.Contains;

        public string Category { get; set; } = null!;

        public string? Subcategory { get; set; }

        public int Priority { get; set; } = 0;

        public bool Enabled { get; set; } = true;

        public bool BuiltIn { get; set; } = false;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        // (opcional)
        [BsonRepresentation(BsonType.String)]
        public RuleOrigin Origin { get; set; } = RuleOrigin.System;
    }
}
