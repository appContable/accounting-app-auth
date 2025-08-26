using System;
using System.Threading.Tasks;
using Mongo2Go;
using MongoDB.Bson;
using MongoDB.Driver;
using AccountCore.DAL.Parser.Models;
using Xunit;

namespace AccountCore.DAL.Parser.Tests;

public class UserCategoryRuleSerializationTests : IDisposable
{
    private readonly MongoDbRunner _runner;
    private readonly IMongoDatabase _db;

    public UserCategoryRuleSerializationTests()
    {
        _runner = MongoDbRunner.Start();
        var settings = MongoClientSettings.FromConnectionString(_runner.ConnectionString);
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
        _db = client.GetDatabase("testdb");
    }

    public void Dispose() => _runner.Dispose();

    [Fact]
    public async Task Id_field_survives_roundtrip()
    {
        var col = _db.GetCollection<UserCategoryRule>("userCategoryRules");
        var rule = new UserCategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = "user1",
            Bank = "bank1",
            Pattern = "pat",
            Category = "cat"
        };
        await col.InsertOneAsync(rule);
        var fetched = await col.Find(x => x.Id == rule.Id).FirstOrDefaultAsync();
        Assert.NotNull(fetched);
        Assert.Equal(rule.Id, fetched!.Id);
    }

    [Fact]
    public async Task CategoryRuleId_survives_roundtrip()
    {
        var col = _db.GetCollection<Transaction>("transactions");
        var ruleId = Guid.NewGuid();
        var tx = new Transaction
        {
            Date = DateTime.UtcNow,
            Description = "desc",
            Amount = 1m,
            Balance = 1m,
            CategoryRuleId = ruleId
        };
        await col.InsertOneAsync(tx);
        var fetched = await col.Find(x => x.CategoryRuleId == ruleId).FirstOrDefaultAsync();
        Assert.NotNull(fetched);
        Assert.Equal(ruleId, fetched!.CategoryRuleId);
    }
}
