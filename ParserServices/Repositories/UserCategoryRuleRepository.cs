using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using ParserDAL.Models;
using ParserServices.Interfaces;

namespace ParserServices.Repositories
{
    public class UserCategoryRuleRepository : IUserCategoryRuleRepository
    {
        private readonly IMongoCollection<UserCategoryRule> _col;

        public UserCategoryRuleRepository(IMongoDatabase db)
        {
            _col = db.GetCollection<UserCategoryRule>("userCategoryRules");
        }

        public async Task UpsertAsync(UserCategoryRule rule, CancellationToken ct = default)
        {
            rule.UpdatedAt = System.DateTime.UtcNow;

            if (rule.Id != Guid.Empty)
            {
                var filterById = Builders<UserCategoryRule>.Filter.Eq(x => x.Id, rule.Id);
                await _col.ReplaceOneAsync(filterById, rule, new ReplaceOptions { IsUpsert = true }, ct);
                return;
            }

            // Si no hay Id, matcheamos por (userId, bank, pattern)
            var filter = Builders<UserCategoryRule>.Filter.Where(x =>
                x.UserId == rule.UserId &&
                x.Bank == rule.Bank &&
                x.Pattern == rule.Pattern
            );

            await _col.ReplaceOneAsync(filter, rule, new ReplaceOptions { IsUpsert = true }, ct);
        }

        public async Task<IReadOnlyList<UserCategoryRule>> GetByUserAndBankAsync(string userId, string bank, CancellationToken ct = default)
        {
            var filter = Builders<UserCategoryRule>.Filter.Where(x => x.UserId == userId && x.Bank == bank);
            var list = await _col.Find(filter).ToListAsync(ct);
            return list;
        }
    }
}
