using AuthDTO.IServices;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using AutoMapper;
using AuthDAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AuthDTO.ReturnsModels;
using AuthDTO.IServices.Result;
using AuthDTO.Entities.User;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;

namespace AuthServices.Services
{
    public class AuthService : IAuthService
    {
        private readonly ILogger<AuthService> _logger;
        private readonly IMapper _mapper;
        private readonly IConfiguration _configuration;
        private readonly IUserService _userService;

        public AuthService(ILogger<AuthService> logger, IMapper mapper, IConfiguration configuration, IUserService userService)
        {
            _logger = logger;
            _mapper = mapper;
            _configuration = configuration;
            _userService = userService;
        }

        public async Task<ServiceResult<ReturnTokenDTO>> Authentication(string username, string password)
        {
            try
            {
                var user = new User();
                using (var dbcontext = new AuthContext(_configuration))
                {
                    user = await dbcontext.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == username.ToLower() && u.IsActive == true);

                    if (user == null)
                    {
                        return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.Argument, "Invalid User/Password");
                    }

                    if (user.IsLock)
                    {
                        return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.Lock, "User");
                    }

                    if (user.NeedsPasswordReset)
                    {
                        return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.ResetPass, "User");
                    }

                    if (!user.VerifyPassword(password))
                    {
                        user.RegistryLoginFail();

                        dbcontext.Update(user);
                        await dbcontext.SaveChangesAsync();

                        return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.Argument, "Invalid User/Password");
                    }

                    // 1. Create Security Token Handler
                    var tokenHandler = new JwtSecurityTokenHandler();

                    // 2. Create Private Key to Encrypted
                    var tokenKey = Encoding.ASCII.GetBytes(_configuration["JWT:Secret"]);


                    // For now there is only one login per user
                    var fullName = string.Empty;

                    fullName = $"{user.FirstName} {user.LastName}";


                    var claims = new List<Claim>()
                {
                    new Claim(ClaimTypes.Name, username),
                    new Claim("FullName", fullName),
                    new Claim(ClaimsPrincipalExtensions.UserId, user.Id),
                    new Claim(ClaimsPrincipalExtensions.Email, user.Email),
                    new Claim(ClaimsPrincipalExtensions.FirstName, user.FirstName ?? string.Empty),
                    new Claim(ClaimsPrincipalExtensions.LastName, user.LastName ?? string.Empty)
                };

                    var roles = new List<string>();

                    if (user.IsSysAdmin)
                    {
                        claims.Add(new Claim(ClaimTypes.Role, ClaimsPrincipalExtensions.AdminRole));
                        roles.Add(ClaimsPrincipalExtensions.AdminRole);
                    }
                    else
                    {
                        foreach (var rol in user.Roles)
                        {
                            claims.Add(new Claim(ClaimTypes.Role, rol.RoleKey));
                            roles.Add(rol.RoleKey);
                        }
                    }

                    //3. Create JETdescriptor
                    var tokenDescriptor = new SecurityTokenDescriptor()
                    {
                        Subject = new ClaimsIdentity(claims),
                        Expires = DateTime.UtcNow.AddMinutes(int.Parse(_configuration["JWT:TokenValidityInMinutes"])),
                        SigningCredentials = new SigningCredentials(
                            new SymmetricSecurityKey(tokenKey), SecurityAlgorithms.HmacSha256Signature),
                    };
                    //4. Create Token
                    var token = tokenHandler.CreateToken(tokenDescriptor);

                    //5. Create RefreshToken
                    var refreshToken = GenerateRefreshToken();

                    _ = int.TryParse(_configuration["JWT:RefreshTokenValidityInDays"], out int refreshTokenValidityInDays);

                    // Si el token esta expirado lo actualizo, sino solo cambio la fecha
                    if (user.RefreshTokenExpiryTime < DateTime.UtcNow || string.IsNullOrEmpty(user.RefreshToken))
                    {
                        user.RefreshToken = refreshToken;
                        user.RefreshTokenExpiryTime = DateTime.Now.AddDays(refreshTokenValidityInDays);
                    }
                    else
                    {
                        refreshToken = user.RefreshToken;
                        user.RefreshTokenExpiryTime = DateTime.Now.AddDays(refreshTokenValidityInDays);
                    }

                    // 6. Return Token from method
                    var returnToken = new ReturnTokenDTO() { Token = tokenHandler.WriteToken(token), Expire = tokenDescriptor.Expires ?? DateTime.Now, Roles = roles, LoginId = user.Id, FullName = fullName, RefreshToken = refreshToken };


                    user.RegistryLoginSucces();

                    dbcontext.Update(user);
                    await dbcontext.SaveChangesAsync();

                    return ServiceResult<ReturnTokenDTO>.Ok(returnToken);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.Authentication");
                return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.InternalErrorCode, e.Message);
            }
        }

        public async Task<ServiceResult<ReturnTokenDTO>> RefreshToken(TokenModelDTO tokenModel)
        {
            try
            {
                string? accessToken = tokenModel.AccessToken;
                string? refreshToken = tokenModel.RefreshToken;


                var principal = GetPrincipalFromExpiredToken(accessToken);
                if (principal == null)
                {
                    return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.Argument, "Invalid access token or refresh token");
                }

                string username = principal.Identity.Name ?? string.Empty;

                var newAccessToken = CreateToken(principal.Identities.FirstOrDefault().Claims.ToList());
                var newRefreshToken = GenerateRefreshToken();

                var user = new User();
                using (var dbcontext = new AuthContext(_configuration))
                {
                    user = await dbcontext.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == username.ToLower() && !u.IsLock);

                    if (user == null || user.RefreshToken != refreshToken || user.RefreshTokenExpiryTime <= DateTime.Now)
                    {
                        return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.Argument, "Invalid access token or refresh token");
                    }

                    //5. Create RefreshToken
                    _ = int.TryParse(_configuration["JWT:RefreshTokenValidityInDays"], out int refreshTokenValidityInDays);

                    if (user.RefreshTokenExpiryTime > DateTime.UtcNow)
                    {
                        newRefreshToken = user.RefreshToken;
                    }

                    user.RefreshToken = newRefreshToken;
                    user.RefreshTokenExpiryTime = DateTime.Now.AddDays(refreshTokenValidityInDays);

                    dbcontext.Update(user);
                    await dbcontext.SaveChangesAsync();
                }

                var objectResult = new ObjectResult(new
                {
                    accessToken = new JwtSecurityTokenHandler().WriteToken(newAccessToken),
                    refreshToken = newRefreshToken
                });

                // For now there is only one login per user
                var fullName = string.Empty;

                fullName = $"{user.FirstName} {user.LastName}";

                // 6. Return Token from method
                var returnToken = new ReturnTokenDTO() { Token = new JwtSecurityTokenHandler().WriteToken(newAccessToken), Expire = DateTime.UtcNow.AddMinutes(int.Parse(_configuration["JWT:TokenValidityInMinutes"])), Roles = user.Roles.Select(r => r.RoleKey), LoginId = user.Id, FullName = fullName, RefreshToken = newRefreshToken };


                return ServiceResult<ReturnTokenDTO>.Ok(returnToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.RefeshToken");
                return ServiceResult<ReturnTokenDTO>.Error(Errors.ErrorsKey.InternalErrorCode, e.Message);
            }
        }

        public async Task<ServiceResult<bool>> ResetPassword(string email)
        {
            try
            {
                if (string.IsNullOrEmpty(email))
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.Argument, "mail");
                }

                var token = string.Empty;
                var userId = string.Empty;

                using (var dbcontext = new AuthContext(_configuration))
                {
                    var user = await dbcontext.Users.FirstOrDefaultAsync(l => l.Email.ToLower() == email.ToLower() && l.IsActive == true && !l.IsSysAdmin);

                    if (user == null)
                    {
                        return ServiceResult<bool>.Error(Errors.ErrorsKey.UserNotExist, "Invalid User");
                    }

                    if (user.ExpirationTokenDate > DateTime.UtcNow && !string.IsNullOrEmpty(user.Token))
                    {
                        // si aun esta vigente uso el mismo token
                        token = user.Token;
                    }
                    else
                    {
                        token = User.GetPassEncode();
                    }

                    token = User.GetPassEncode();

                    user.Token = token;
                    userId = user.Id;

                    user.ExpirationTokenDate = DateTime.UtcNow.AddHours(1);

                    dbcontext.Users.Update(user);

                    await dbcontext.SaveChangesAsync();
                }

                var urlBase = _configuration["UiUrlBase"];

                var link = $"{urlBase}set-new-password?UserId={userId}&code={token}";

                //var mail = await _emailService.Send(email, "Resetear Contraseña",link);

                return ServiceResult<bool>.Ok(true);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.ResetPassword");
                return ServiceResult<bool>.Error(Errors.ErrorsKey.InternalErrorCode, e.Message);
            }
        }

        public async Task<ServiceResult<bool>> SetNewPassword(string userId, string codeBase64, SetPasswordDTO setPasswordDTO)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(codeBase64))
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.Argument, "SetNewPasswordDTO");
                }

                if (string.IsNullOrEmpty(setPasswordDTO?.Password))
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.Argument, "Password");
                }

                if (setPasswordDTO?.Password != setPasswordDTO?.ConfirmPassword)
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.Argument, "ConfirmPassword");
                }

                if (setPasswordDTO?.Password.Length < 4 )
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.WeakPassword, "WeakPassword");
                }


                using (var dbcontext = new AuthContext(_configuration))
                {
                    var user = await dbcontext.Users.FirstOrDefaultAsync(l => l.Id == userId && l.Token == codeBase64 && l.IsActive == true && !l.IsSysAdmin);

                    if (user == null)
                    {
                        return ServiceResult<bool>.Error(Errors.ErrorsKey.InvalidInvitation, "Invalid Token");
                    }

                    if (user.ExpirationTokenDate <= DateTime.UtcNow)
                    {
                        return ServiceResult<bool>.Error(Errors.ErrorsKey.InvitationExpired, "Expired Token");
                    }

                    user.SetPassword(setPasswordDTO?.ConfirmPassword);

                    dbcontext.Users.Update(user);

                    await dbcontext.SaveChangesAsync();
                }

                return ServiceResult<bool>.Ok(true);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.SetNewPassword");
                return ServiceResult<bool>.Error(Errors.ErrorsKey.InternalErrorCode, e.Message);
            }
        }


        private JwtSecurityToken CreateToken(List<Claim> authClaims)
        {
            var authSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"]));
            _ = int.TryParse(_configuration["JWT:TokenValidityInMinutes"], out int tokenValidityInMinutes);

            var token = new JwtSecurityToken(
                issuer: _configuration["JWT:ValidIssuer"],
                audience: _configuration["JWT:ValidAudience"],
                expires: DateTime.Now.AddMinutes(tokenValidityInMinutes),
                claims: authClaims,
                signingCredentials: new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256)
                );

            return token;
        }

        private static string GenerateRefreshToken()
        {
            var randomNumber = new byte[64];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        private ClaimsPrincipal? GetPrincipalFromExpiredToken(string? token)
        {
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Secret"])),
                ValidateLifetime = false
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out SecurityToken securityToken);
            if (securityToken is not JwtSecurityToken jwtSecurityToken || !jwtSecurityToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
                throw new SecurityTokenException("Invalid token");

            return principal;

        }
    }
}
