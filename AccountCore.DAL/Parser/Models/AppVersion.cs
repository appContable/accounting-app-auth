using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AccountCore.DAL.Parser.Models
{
    public class AppVersion
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        public string Version { get; set; } = null!;

        public DateTime ReleaseDate { get; set; } = DateTime.UtcNow;

        public List<string> Changes { get; set; } = new();

        public bool IsCurrent { get; set; }
    }
}
