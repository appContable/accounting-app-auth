using MongoDB.Driver;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Repositories
{
    public class UserCategoryRuleRepository : IUserCategoryRuleRepository
    {
        private readonly IMongoCollection<UserCategoryRule> _col;

        public UserCategoryRuleRepository(IMongoDatabase db)
        {
            _col = db.GetCollection<UserCategoryRule>("UserCategoryRules");
        }

        // AccountCore.Services/Parser/Repositories/UserCategoryRuleRepository.cs
        public Task EnsureIndexesAsync(CancellationToken ct = default)
        {
            var keys = Builders<UserCategoryRule>.IndexKeys
                .Ascending(x => x.UserId)
                .Ascending(x => x.Bank)
                .Ascending(x => x.Pattern)
                .Ascending(x => x.PatternType);

            var model = new CreateIndexModel<UserCategoryRule>(
                keys,
                new CreateIndexOptions { Unique = true, Name = "UX_User_Bank_Pattern_Type" });

            return _col.Indexes.CreateOneAsync(model, null, ct);
        }


        public async Task UpsertAsync(UserCategoryRule rule, CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;

            var filter =
                rule.Id != Guid.Empty
                    ? Builders<UserCategoryRule>.Filter.Eq(x => x.Id, rule.Id)
                    : Builders<UserCategoryRule>.Filter.And(
                        Builders<UserCategoryRule>.Filter.Eq(x => x.UserId, rule.UserId),
                        Builders<UserCategoryRule>.Filter.Eq(x => x.Bank, rule.Bank),
                        Builders<UserCategoryRule>.Filter.Eq(x => x.Pattern, rule.Pattern),
                        Builders<UserCategoryRule>.Filter.Eq(x => x.PatternType, rule.PatternType)
                      );

            var update = Builders<UserCategoryRule>.Update
                // cambios en updates (y también aplican en insert por upsert)
                .Set(x => x.Category, rule.Category)
                .Set(x => x.Subcategory, rule.Subcategory)
                .Set(x => x.Priority, rule.Priority == 0 ? 100 : rule.Priority)
                .Set(x => x.Active, rule.Active)       // <-- mantener aquí
                .Set(x => x.UpdatedAt, now)
                // solo al insertar (¡sin 'Active' aquí!)
                .SetOnInsert(x => x.Id, rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id)
                .SetOnInsert(x => x.UserId, rule.UserId)
                .SetOnInsert(x => x.Bank, rule.Bank)
                .SetOnInsert(x => x.Pattern, rule.Pattern)
                .SetOnInsert(x => x.PatternType, rule.PatternType)
                .SetOnInsert(x => x.CreatedAt, now)
                .SetOnInsert(x => x.HitCount, 0);

            await _col.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
        }


        public async Task<IReadOnlyList<UserCategoryRule>> GetByUserAndBankAsync(string userId, string bank, CancellationToken ct = default)
        {
            var filter = Builders<UserCategoryRule>.Filter.Where(x => x.UserId == userId && x.Bank == bank);
            var list = await _col.Find(filter).ToListAsync(ct);
            return list;
        }
    }
}
