namespace AuthDTO.ReturnsModels
{
    public class ReturnTokenDTO
    {
        public string? Token { get; set; }

        public DateTime Expire { get; set; }

        public IEnumerable<string>? Roles { get; set; }

        public string LoginId { get; set; }

        public string FullName { get; set; }

        public string RefreshToken { get; set; }

    }
}
