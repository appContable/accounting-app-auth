using AuthDTO.Entities;

namespace AuthDTO.Parameters
{
    public partial class ResetPasswordParameterDTO
    {
        public string NewPassword { get; set; }
        public string Code { get; set; } = null!;
    }
}
