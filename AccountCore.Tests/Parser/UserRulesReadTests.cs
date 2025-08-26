using System;
using System.Threading.Tasks;
using Mongo2Go;
using MongoDB.Bson;
using MongoDB.Driver;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Repositories;
using Xunit;

namespace AccountCore.Tests.Parser;

public class UserRulesReadTests : IDisposable
{
    private readonly MongoDbRunner _runner;
    private readonly IMongoDatabase _db;
    private readonly UserCategoryRuleRepository _repo;

    public UserRulesReadTests()
    {
        _runner = MongoDbRunner.Start();
        var settings = MongoClientSettings.FromConnectionString(_runner.ConnectionString);
        var guidProperty = typeof(MongoClientSettings).GetProperty("GuidRepresentation");
        if (guidProperty is not null)
        {
            guidProperty.SetValue(settings, GuidRepresentation.CSharpLegacy);
        }
        var defaultsGuidProperty = typeof(MongoDefaults).GetProperty("GuidRepresentation");
        if (defaultsGuidProperty is not null)
        {
            defaultsGuidProperty.SetValue(null, GuidRepresentation.CSharpLegacy);
        }
        var client = new MongoClient(settings);
        _db = client.GetDatabase("testdb");
        _repo = new UserCategoryRuleRepository(_db);
    }

    public void Dispose() => _runner.Dispose();

    [Fact]
    public async Task GetByUserAndBankAsync_returns_inserted_rule()
    {
        var rule = new UserCategoryRule
        {
            Id = Guid.NewGuid(),
            UserId = "user1",
            Bank = "bank1",
            Pattern = "pattern",
            Category = "category",
            Subcategory = "subcategory",
            Priority = 10,
            Active = true,
            PatternType = RulePatternType.Contains
        };

        await _repo.UpsertAsync(rule);

        var list = await _repo.GetByUserAndBankAsync(rule.UserId, rule.Bank);
        var fetched = Assert.Single(list);

        Assert.Equal(rule.Id, fetched.Id);
        Assert.Equal(rule.UserId, fetched.UserId);
        Assert.Equal(rule.Bank, fetched.Bank);
        Assert.Equal(rule.Pattern, fetched.Pattern);
        Assert.Equal(rule.Category, fetched.Category);
        Assert.Equal(rule.Subcategory, fetched.Subcategory);
        Assert.Equal(rule.Priority, fetched.Priority);
        Assert.Equal(rule.Active, fetched.Active);
        Assert.Equal(rule.PatternType, fetched.PatternType);
    }
}
