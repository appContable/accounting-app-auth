using System.Security.Cryptography;

namespace AccountCore.DAL.Auth.Models
{
    public partial class User
    {
        public string? Id { get; set; }

        public string? FirstName { get; set; }

        public string? LastName { get; set; }

        public string Email { get; set; } = null!;

        public bool IsLock { get; set; } = false;

        public List<RoleUser>? Roles { get; set; }

        public bool IsSysAdmin { get; set; } = false;

        public string? Hash { get; set; }

        public byte[]? BPassword { get; set; }

        public bool NeedsPasswordReset { get; set; }

        public int FailedLoginCount { get; set; } = 0;

        public DateTime CreationDate { get; set; }

        public DateTime? LastLogin { get; set; }
        public DateTime? LastLoginFail { get; set; }
        public DateTime LastPasswordChange { get; set; }

        public string? RefreshToken { get; set; }
        public string? Token { get; set; }

        public DateTime? RefreshTokenExpiryTime { get; set; }
        public DateTime? ExpirationTokenDate { get; set; }

        public bool? IsActive { get; set; } = false;

        public byte[]? Salt { get; set; }

        public void AddRole(Role newRole)
        {
            Roles ??= new List<RoleUser>();

            var oldRole = Roles.FirstOrDefault(r => r.RoleId == newRole.Id);

            if (oldRole != null)
            {
                if (!oldRole.Enable)
                {
                    oldRole.Enable = true;
                }

                return;
            }

            Roles.Add(new RoleUser
            {
                CreationDate = DateTime.UtcNow,
                Enable = true,
                RoleId = newRole.Id,
                RoleKey = newRole.RoleKey,
                RoleName = newRole.Name,
            }
            );
        }

        public void RegistryLoginFail()
        {
            FailedLoginCount++;
            LastLoginFail = DateTime.UtcNow;

            IsLock = FailedLoginCount > 10;
        }

        public void RegistryLoginSucces()
        {
            FailedLoginCount = 0;
            LastLoginFail = null;
            LastLogin = DateTime.UtcNow;
        }

        public void SetPassword(string? confirmPassword)
        {
            if (string.IsNullOrEmpty(confirmPassword))
            {
                return;
            }

            using (var deriveBytes = new Rfc2898DeriveBytes(confirmPassword, 20, 100000, HashAlgorithmName.SHA256))
            {
                Salt = deriveBytes.Salt;
                BPassword = deriveBytes.GetBytes(20);  // derive a 20-byte key
            }

            IsLock = false;
            FailedLoginCount = 0;
            RefreshToken = null;
            RefreshTokenExpiryTime = null;
        }

        public bool VerifyPassword(string password)
        {

            if (BPassword == null || Salt == null)
            {
                //this.SetPassword("Ab123456");
                return false;
            }

            using (var deriveBytes = new Rfc2898DeriveBytes(password, Salt, 100000, HashAlgorithmName.SHA256))
            {
                byte[] newKey = deriveBytes.GetBytes(20);  // derive a 20-byte key

                return newKey.SequenceEqual(BPassword);
            }
        }

        public static string GetPassEncode()
        {
            byte[] bytes;
            string bytesBase64Url; // NOTE: This is Base64Url-encoded, not Base64-encoded, so it is safe to use this in a URL, but be sure to convert it to Base64 first when decoding it.
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {

                bytes = new Byte[12]; // Use a multiple of 3 (e.g. 3, 6, 12) to prevent output with trailing padding '=' characters in Base64).
                rng.GetBytes(bytes);

                // The `.Replace()` methods convert the Base64 string returned from `ToBase64String` to Base64Url.
                bytesBase64Url = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
            }

            return bytesBase64Url;
        }
    }
}
