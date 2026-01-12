using Microsoft.EntityFrameworkCore;
using AccountCore.DAL.Auth.Models;
using AccountCore.Services.Auth.Interfaces;
using AccountCore.DTO.Auth.Parameters;

namespace AccountCore.Services.Auth.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly AuthContext _context;

        public UserRepository(AuthContext context)
        {
            _context = context;
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == email.ToLower());
        }

        public async Task<User?> GetByCuitAsync(string cuit)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Cuit == cuit);
        }

        public async Task<User?> GetByIdAsync(string id)
        {
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Id == id);
        }

        public async Task<IEnumerable<User>> FindAsync(string? searchValue)
        {
            var parameters = UserParameterDTO.LoadParameters(searchValue);
            var query = _context.Users.AsQueryable().Where(u => !u.IsSysAdmin);

            foreach (var parameter in parameters)
            {
                if (!string.IsNullOrEmpty(parameter.Name))
                {
                    query = query.Where(u => 
                        (u.FirstName != null && u.FirstName.ToLower().Contains(parameter.Name.ToLower())) || 
                        (u.LastName != null && u.LastName.ToLower().Contains(parameter.Name.ToLower())));
                }

                if (!string.IsNullOrEmpty(parameter.Email))
                {
                    query = query.Where(u => u.Email.ToLower().Contains(parameter.Email.ToLower()));
                }
            }

            return await query.OrderBy(u => u.LastName).ToListAsync();
        }

        public async Task<User> CreateAsync(User user)
        {
            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            return user;
        }

        public async Task<User> UpdateAsync(User user)
        {
            _context.Users.Update(user);
            await _context.SaveChangesAsync();
            return user;
        }

        public async Task DeleteAsync(string id)
        {
            var user = await GetByIdAsync(id);
            if (user != null)
            {
                _context.Users.Remove(user);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<bool> EmailExistsAsync(string email, string? excludeUserId = null)
        {
            var query = _context.Users.AsQueryable().Where(u => u.Email.Equals(email));

            if (!string.IsNullOrEmpty(excludeUserId))
            {
                query = query.Where(u => u.Id != excludeUserId);
            }

            return await query.AnyAsync();
        }

        public async Task<bool> CuitExistsAsync(string cuit, string? excludeUserId = null)
        {
            var query = _context.Users.AsQueryable().Where(u => u.Cuit.Equals(cuit));

            if (!string.IsNullOrEmpty(excludeUserId))
            {
                query = query.Where(u => u.Id != excludeUserId);
            }

            return await query.AnyAsync();
        }
    }
}