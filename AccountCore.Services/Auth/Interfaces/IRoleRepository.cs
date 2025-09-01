using AccountCore.DAL.Auth.Models;

namespace AccountCore.Services.Auth.Interfaces
{
    public interface IRoleRepository
    {
        Task<IEnumerable<Role>> GetEnabledRolesAsync();
        Task<IEnumerable<Role>> GetRolesByIdsAsync(IEnumerable<string> roleIds);
        Task<Role?> GetByIdAsync(string id);
    }
}