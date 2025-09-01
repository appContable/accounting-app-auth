
namespace AccountCore.DTO.Auth.Parameters
{
    public partial class ResetPasswordParameterDTO
    {
        public string? NewPassword { get; set; }
        public string Code { get; set; } = null!;
    }
}
