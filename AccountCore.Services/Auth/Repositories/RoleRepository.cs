using Microsoft.EntityFrameworkCore;
using AccountCore.DAL.Auth.Models;
using AccountCore.Services.Auth.Interfaces;

namespace AccountCore.Services.Auth.Repositories
{
    public class RoleRepository : IRoleRepository
    {
        private readonly AuthContext _context;

        public RoleRepository(AuthContext context)
        {
            _context = context;
        }

        public async Task<IEnumerable<Role>> GetEnabledRolesAsync()
        {
            return await _context.Roles
                .Where(r => r.IsEnabled)
                .ToListAsync();
        }

        public async Task<IEnumerable<Role>> GetRolesByIdsAsync(IEnumerable<string> roleIds)
        {
            return await _context.Roles
                .Where(r => r.IsEnabled && roleIds.Contains(r.RoleKey))
                .ToListAsync();
        }

        public async Task<Role?> GetByIdAsync(string id)
        {
            return await _context.Roles
                .FirstOrDefaultAsync(r => r.Id == id);
        }
    }
}