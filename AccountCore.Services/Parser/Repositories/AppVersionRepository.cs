using MongoDB.Driver;
using AccountCore.DAL.Parser.Models;
using AccountCore.Services.Parser.Interfaces;

namespace AccountCore.Services.Parser.Repositories
{
    public class AppVersionRepository : IAppVersionRepository
    {
        private readonly IMongoCollection<AppVersion> _collection;

        public AppVersionRepository(IMongoDatabase db)
        {
            _collection = db.GetCollection<AppVersion>("AppVersions");
        }

        public async Task<List<AppVersion>> GetAllAsync()
        {
            return await _collection.Find(_ => true)
                .SortByDescending(v => v.ReleaseDate)
                .ToListAsync();
        }

        public async Task<AppVersion?> GetLatestAsync()
        {
            return await _collection.Find(_ => true)
                .SortByDescending(v => v.ReleaseDate)
                .FirstOrDefaultAsync();
        }

        public async Task CreateAsync(AppVersion version)
        {
            await _collection.InsertOneAsync(version);
        }

        public async Task<bool> ExistsAsync(string version)
        {
            return await _collection.Find(v => v.Version == version).AnyAsync();
        }
    }
}
