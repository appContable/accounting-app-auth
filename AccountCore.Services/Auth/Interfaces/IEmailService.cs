using AccountCore.DAL.Auth.Models;
using AccountCore.DTO.Auth.IServices.Result;

namespace AccountCore.Services.Auth.Interfaces
{
    public interface IEmailService
    {
        Task<ServiceResult<bool>> SendWelcomeEmailAsync(User user, string activationLink);

        Task<ServiceResult<bool>> SendResetPasswordEmailAsync(User user, string resetLink);
    }
}
