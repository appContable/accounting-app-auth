namespace AccountCore.DTO.Auth.ReturnsModels
{
    public class ReturnTokenDTO
    {
        public string? Token { get; set; }

        public DateTime Expire { get; set; }

        public IEnumerable<string>? Roles { get; set; }

        public string LoginId { get; set; } = string.Empty;

        public string FullName { get; set; } = string.Empty;

        public string RefreshToken { get; set; } = string.Empty;

    }
}
