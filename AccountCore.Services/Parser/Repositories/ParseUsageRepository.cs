using MongoDB.Bson;
using MongoDB.Driver;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Repositories
{
    public class ParseUsageRepository : IParseUsageRepository
    {
        private readonly IMongoCollection<ParseUsage> _collection;

        public ParseUsageRepository(IMongoDatabase db)
        {
            _collection = db.GetCollection<ParseUsage>("Usage");
        }

        public async Task<List<ParseUsage>> GetAllAsync()
            => await _collection.Find(_ => true).ToListAsync();

        public async Task<ParseUsage?> GetByIdAsync(ObjectId id)
            => await _collection.Find(u => u.Id == id).FirstOrDefaultAsync();

        public async Task CreateAsync(ParseUsage Usage)
            => await _collection.InsertOneAsync(Usage);

        public async Task UpdateAsync(ParseUsage Usage)
            => await _collection.ReplaceOneAsync(u => u.Id == Usage.Id, Usage);

        public async Task DeleteAsync(ObjectId id)
            => await _collection.DeleteOneAsync(u => u.Id == id);

        public async Task<int> CountByUserAsync(string userId, DateTime start, DateTime end)
        {
            var filter = Builders<ParseUsage>.Filter.Eq(u => u.UserId, userId) &
                         Builders<ParseUsage>.Filter.Gte(u => u.ParsedAt, start) &
                         Builders<ParseUsage>.Filter.Lte(u => u.ParsedAt, end);

            return (int)await _collection.CountDocumentsAsync(filter);
        }
    }
}
