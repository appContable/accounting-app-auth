using AccountCore.DAL.Auth.Models;

namespace AccountCore.Services.Auth.Interfaces
{
    public interface IUserRepository
    {
        Task<User?> GetByEmailAsync(string email);
        Task<User?> GetByCuitAsync(string cuit);
        Task<User?> GetByIdAsync(string id);
        Task<IEnumerable<User>> FindAsync(string? searchValue);
        Task<User> CreateAsync(User user);
        Task<User> UpdateAsync(User user);
        Task DeleteAsync(string id);
        Task<bool> EmailExistsAsync(string email, string? excludeUserId = null);
        Task<bool> CuitExistsAsync(string cuit, string? excludeUserId = null);
    }
}