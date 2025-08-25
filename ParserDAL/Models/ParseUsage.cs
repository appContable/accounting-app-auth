using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ParserDAL.Models
{
    public class ParseUsage
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public string UserId { get; set; } = string.Empty;
        public string Bank { get; set; } = string.Empty;

        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime ParsedAt { get; set; } = DateTime.UtcNow;
    }
}
