using System;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using AccountCore.DAL.Parser.Models;

namespace AccountCore.Migrations
{
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var connectionString = Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017";
            var databaseName = Environment.GetEnvironmentVariable("MONGO_DB") ?? "parserdb";

            var settings = MongoClientSettings.FromConnectionString(connectionString);
            var guidProperty = typeof(MongoClientSettings).GetProperty("GuidRepresentation");
            if (guidProperty is not null)
            {
                guidProperty.SetValue(settings, GuidRepresentation.Standard);
            }
            var defaultsGuidProperty = typeof(MongoDefaults).GetProperty("GuidRepresentation");
            if (defaultsGuidProperty is not null)
            {
                defaultsGuidProperty.SetValue(null, GuidRepresentation.Standard);
            }

            var client = new MongoClient(settings);
            var db = client.GetDatabase(databaseName);

            await MigrateUserCategoryRules(db);
            await MigrateTransactions(db);

            Console.WriteLine("Guid migration completed.");
        }

        private static async Task MigrateUserCategoryRules(IMongoDatabase db)
        {
            var col = db.GetCollection<BsonDocument>("userCategoryRules");
            var docs = await col.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            foreach (var doc in docs)
            {
                var id = doc.GetValue("_id", BsonNull.Value);
                if (id.BsonType == BsonType.Binary && id.AsBsonBinaryData.SubType == BsonBinarySubType.UuidLegacy)
                {
                    var guid = id.AsGuid;
                    doc["_id"] = new BsonBinaryData(guid, GuidRepresentation.Standard);
                    await col.ReplaceOneAsync(new BsonDocument("_id", id), doc);
                }
            }
        }

        private static async Task MigrateTransactions(IMongoDatabase db)
        {
            var col = db.GetCollection<BsonDocument>("transactions");
            var docs = await col.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            foreach (var doc in docs)
            {
                if (doc.TryGetValue("CategoryRuleId", out var val) && val.BsonType == BsonType.Binary && val.AsBsonBinaryData.SubType == BsonBinarySubType.UuidLegacy)
                {
                    var guid = val.AsGuid;
                    doc["CategoryRuleId"] = new BsonBinaryData(guid, GuidRepresentation.Standard);
                    await col.ReplaceOneAsync(new BsonDocument("_id", doc["_id"]), doc);
                }
            }
        }
    }
}
