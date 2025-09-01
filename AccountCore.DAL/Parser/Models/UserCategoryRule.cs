using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.ComponentModel.DataAnnotations;

namespace AccountCore.DAL.Parser.Models
{
    public class UserCategoryRule
    {
        [BsonId]
        [BsonGuidRepresentation(GuidRepresentation.CSharpLegacy)] // BinData(3)
        public Guid Id { get; set; }

        [Required, MaxLength(128)]
        public string UserId { get; set; } = default!;

        [Required, MaxLength(64)]
        public string Bank { get; set; } = default!;

        [Required, MaxLength(512)]
        public string Pattern { get; set; } = default!;

        [BsonRepresentation(BsonType.String)]
        public RulePatternType PatternType { get; set; } = RulePatternType.Contains;

        [Required, MaxLength(128)]
        public string Category { get; set; } = default!;

        [MaxLength(128)]
        public string? Subcategory { get; set; }

        public int Priority { get; set; } = 100;

        public bool Active { get; set; } = true;

        public int HitCount { get; set; }
        public DateTime? LastHitAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        [BsonRepresentation(BsonType.String)]
        public RuleOrigin Origin { get; set; } = RuleOrigin.Manual;
    }
}
