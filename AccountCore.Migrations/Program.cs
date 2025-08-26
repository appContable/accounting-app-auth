using System;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AccountCore.Migrations;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("MONGO_URI") ?? "mongodb://localhost:27017";
        var databaseName = Environment.GetEnvironmentVariable("MONGO_DB") ?? "parserdb";
        var settings = MongoClientSettings.FromConnectionString(connectionString);
        var client = new MongoClient(settings);
        var db = client.GetDatabase(databaseName);

        await MigrateUserCategoryRules(db);

        Console.WriteLine("Guid migration completed.");
    }

    private static async Task MigrateUserCategoryRules(IMongoDatabase db)
    {
        var collection = db.GetCollection<BsonDocument>("userCategoryRules");
        var documents = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        foreach (var document in documents)
        {
            var originalId = document.GetValue("_id");
            RewriteLegacyGuids(document);
            await collection.ReplaceOneAsync(new BsonDocument("_id", originalId), document);
        }
    }

    private static void RewriteLegacyGuids(BsonValue value)
    {
        if (value is BsonDocument doc)
        {
            foreach (var element in doc.Elements.ToList())
            {
                doc[element.Name] = ConvertValue(element.Value);
            }
        }
        else if (value is BsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                array[i] = ConvertValue(array[i]);
            }
        }
    }

    private static BsonValue ConvertValue(BsonValue value)
    {
        if (value is BsonDocument doc)
        {
            RewriteLegacyGuids(doc);
            return doc;
        }

        if (value is BsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                array[i] = ConvertValue(array[i]);
            }
            return array;
        }

        if (value.BsonType == BsonType.Binary && value.AsBsonBinaryData.SubType == BsonBinarySubType.UuidLegacy)
        {
            var guid = value.AsBsonBinaryData.ToGuid(GuidRepresentation.CSharpLegacy);
            return new BsonBinaryData(guid, GuidRepresentation.Standard);
        }

        return value;
    }
}

