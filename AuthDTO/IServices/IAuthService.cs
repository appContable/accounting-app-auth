using AuthDTO.Entities.User;
using AuthDTO.IServices.Result;
using AuthDTO.ReturnsModels;


namespace AuthDTO.IServices
{
    public interface IAuthService
    {
        Task<ServiceResult<ReturnTokenDTO>> Authentication(string username, string password);
        Task<ServiceResult<ReturnTokenDTO>> RefreshToken(TokenModelDTO tokenModel);

        Task<ServiceResult<bool>> SetNewPassword(string userId, string codeBase64, SetPasswordDTO setPasswordDTO);

        Task<ServiceResult<bool>> ResetPassword(string mail);
    }
}
