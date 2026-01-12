using AccountCore.DAL.Parser.Models;

namespace AccountCore.Services.Parser.Interfaces
{
    public interface IAppVersionRepository
    {
        Task<List<AppVersion>> GetAllAsync();
        Task<AppVersion?> GetLatestAsync();
        Task CreateAsync(AppVersion version);
        Task<bool> ExistsAsync(string version);
    }
}
