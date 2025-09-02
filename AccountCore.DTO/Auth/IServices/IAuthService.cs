using AccountCore.DTO.Auth.Entities.User;
using AccountCore.DTO.Auth.IServices.Result;
using AccountCore.DTO.Auth.ReturnsModels;


namespace AccountCore.DTO.Auth.IServices
{
    public interface IAuthService
    {
        Task<ServiceResult<ReturnTokenDTO>> Authentication(string username, string password);
        Task<ServiceResult<ReturnTokenDTO>> RefreshToken(TokenModelDTO tokenModel);

        Task<ServiceResult<bool>> SetNewPassword(string userId, string codeBase64, SetPasswordDTO setPasswordDTO);

        Task<ServiceResult<bool>> ResetPassword(string mail);
    }
}
