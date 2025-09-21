using AccountCore.DTO.Auth.IServices;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using AutoMapper;
using AccountCore.DAL.Auth.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AccountCore.DTO.Auth.ReturnsModels;
using AccountCore.DTO.Auth.IServices.Result;
using AccountCore.DTO.Auth.Entities.User;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using AccountCore.Services.Auth.Errors;
using AccountCore.Services.Auth.Interfaces;
using AccountCore.DTO.Auth.Validation;
using AccountCore.DTO.Auth.Configuration;
using Microsoft.Extensions.Options;

namespace AccountCore.Services.Auth.Services
{
    public class AuthService : IAuthService
    {
        private readonly ILogger<AuthService> _logger;
        private readonly IMapper _mapper;
        private readonly JwtSettings _jwtSettings;
        private readonly IUserRepository _userRepository;

        public AuthService(ILogger<AuthService> logger, IMapper mapper, IOptions<JwtSettings> jwtOptions, IUserRepository userRepository)
        {
            _logger = logger;
            _mapper = mapper;
            _jwtSettings = jwtOptions.Value;
            _userRepository = userRepository;
        }

        public async Task<ServiceResult<ReturnTokenDTO>> Authentication(string username, string password)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.Argument, "Email and password are required");
                }

                var user = await _userRepository.GetByEmailAsync(username);
                if (user == null)
                {
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.Argument, "Invalid User/Password");
                }

                if (user.IsLock)
                {
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.Lock, "User");
                }

                if (user.NeedsPasswordReset)
                {
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.ResetPass, "User");
                }

                if (!user.VerifyPassword(password))
                {
                    user.RegistryLoginFail();
                    await _userRepository.UpdateAsync(user);
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.Argument, "Invalid User/Password");
                }

                // Create JWT token
                var tokenHandler = new JwtSecurityTokenHandler();
                var secret = _jwtSettings.Secret;
                var tokenKey = Encoding.ASCII.GetBytes(secret);

                var fullName = $"{user.FirstName ?? ""} {user.LastName ?? ""}".Trim();

                var claims = new List<Claim>()
                {
                    new Claim(ClaimTypes.Name, username),
                    new Claim("FullName", fullName),
                    new Claim(ClaimsPrincipalExtensions.UserId, user.Id ?? ""),
                    new Claim(ClaimsPrincipalExtensions.Email, user.Email ?? ""),
                    new Claim(ClaimsPrincipalExtensions.FirstName, user.FirstName ?? ""),
                    new Claim(ClaimsPrincipalExtensions.LastName, user.LastName ?? "")
                };

                var roles = new List<string>();

                if (user.IsSysAdmin)
                {
                    claims.Add(new Claim(ClaimTypes.Role, ClaimsPrincipalExtensions.AdminRole));
                    roles.Add(ClaimsPrincipalExtensions.AdminRole);
                }
                else
                {
                    var userRoles = user.Roles ?? Enumerable.Empty<RoleUser>();
                    foreach (var rol in userRoles.Where(r => r.Enable))
                    {
                        claims.Add(new Claim(ClaimTypes.Role, rol.RoleKey));
                        roles.Add(rol.RoleKey);
                    }
                }

                var tokenDescriptor = new SecurityTokenDescriptor()
                {
                    Subject = new ClaimsIdentity(claims),
                    Expires = DateTime.UtcNow.AddMinutes(_jwtSettings.TokenValidityInMinutes),
                    SigningCredentials = new SigningCredentials(
                        new SymmetricSecurityKey(tokenKey), SecurityAlgorithms.HmacSha256Signature),
                };

                var token = tokenHandler.CreateToken(tokenDescriptor);
                var refreshToken = GenerateRefreshToken();

                // Update refresh token if expired or missing
                if (user.RefreshTokenExpiryTime < DateTime.UtcNow || string.IsNullOrEmpty(user.RefreshToken))
                {
                    user.RefreshToken = refreshToken;
                    user.RefreshTokenExpiryTime = DateTime.Now.AddDays(_jwtSettings.RefreshTokenValidityInDays);
                }
                else
                {
                    refreshToken = user.RefreshToken!;
                    user.RefreshTokenExpiryTime = DateTime.Now.AddDays(_jwtSettings.RefreshTokenValidityInDays);
                }

                var returnToken = new ReturnTokenDTO()
                {
                    Token = tokenHandler.WriteToken(token),
                    Expire = tokenDescriptor.Expires ?? DateTime.Now,
                    Roles = roles,
                    LoginId = user.Id ?? "",
                    FullName = fullName,
                    RefreshToken = refreshToken
                };

                user.RegistryLoginSucces();
                await _userRepository.UpdateAsync(user);

                return ServiceResult<ReturnTokenDTO>.Ok(returnToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.Authentication");
                return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.InternalErrorCode, e.Message);
            }
        }

        public async Task<ServiceResult<ReturnTokenDTO>> RefreshToken(TokenModelDTO tokenModel)
        {
            try
            {
                string? accessToken = tokenModel.AccessToken;
                string? refreshToken = tokenModel.RefreshToken;

                var principal = GetPrincipalFromExpiredToken(accessToken);
                if (principal?.Identity == null)
                {
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.Argument, "Invalid access token or refresh token");
                }

                var principalIdentity = principal.Identities.FirstOrDefault();
                if (principalIdentity == null)
                {
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.Argument, "Invalid access token or refresh token");
                }

                string username = principal.Identity.Name ?? string.Empty;

                var newAccessToken = CreateToken(principalIdentity.Claims.ToList());
                var newRefreshToken = GenerateRefreshToken();

                var user = await _userRepository.GetByEmailAsync(username);
                if (user == null || user.RefreshToken != refreshToken || user.RefreshTokenExpiryTime <= DateTime.Now)
                {
                    return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.Argument, "Invalid access token or refresh token");
                }

                if (user.RefreshTokenExpiryTime > DateTime.UtcNow)
                {
                    newRefreshToken = user.RefreshToken;
                }

                user.RefreshToken = newRefreshToken;
                user.RefreshTokenExpiryTime = DateTime.Now.AddDays(_jwtSettings.RefreshTokenValidityInDays);
                await _userRepository.UpdateAsync(user);

                var fullName = $"{user.FirstName ?? ""} {user.LastName ?? ""}".Trim();
                var userRoles = user.Roles ?? Enumerable.Empty<RoleUser>();

                var returnToken = new ReturnTokenDTO()
                {
                    Token = new JwtSecurityTokenHandler().WriteToken(newAccessToken),
                    Expire = DateTime.UtcNow.AddMinutes(_jwtSettings.TokenValidityInMinutes),
                    Roles = userRoles.Select(r => r.RoleKey ?? ""),
                    LoginId = user.Id ?? "",
                    FullName = fullName,
                    RefreshToken = newRefreshToken ?? string.Empty
                };

                return ServiceResult<ReturnTokenDTO>.Ok(returnToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.RefeshToken");
                return ServiceResult<ReturnTokenDTO>.Error(ErrorsKey.InternalErrorCode, e.Message);
            }
        }

        public async Task<ServiceResult<bool>> ResetPassword(string email)
        {
            try
            {
                if (string.IsNullOrEmpty(email))
                {
                    return ServiceResult<bool>.Error(ErrorsKey.Argument, "mail");
                }

                if (!email.IsValidEmail())
                {
                    return ServiceResult<bool>.Error(ErrorsKey.Argument, "Invalid email format");
                }

                var user = await _userRepository.GetByEmailAsync(email);
                if (user == null)
                {
                    return ServiceResult<bool>.Error(ErrorsKey.UserNotExist, "Invalid User");
                }

                string token;
                if (user.ExpirationTokenDate > DateTime.UtcNow && !string.IsNullOrEmpty(user.Token))
                {
                    token = user.Token;
                }
                else
                {
                    token = User.GetPassEncode();
                }

                user.Token = token;
                user.ExpirationTokenDate = DateTime.UtcNow.AddHours(1);
                await _userRepository.UpdateAsync(user);

                var urlBase = "http://localhost:4200"; // TODO: Get from ApiSettings
                var link = $"{urlBase}/set-new-password?UserId={user.Id}&code={token}";

                // TODO: Send email with reset link
                // await _emailService.Send(email, "Resetear Contraseña", link);

                return ServiceResult<bool>.Ok(true);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.ResetPassword");
                return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, e.Message);
            }
        }

        public async Task<ServiceResult<bool>> SetNewPassword(string userId, string codeBase64, SetPasswordDTO setPasswordDTO)
        {
            try
            {
                if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(codeBase64))
                {
                    return ServiceResult<bool>.Error(ErrorsKey.Argument, "UserId and code are required");
                }

                var validationResults = ValidationExtensions.ValidateObject(setPasswordDTO);
                if (validationResults.Any())
                {
                    var errors = validationResults.Select(v => new KeyValuePair<string, string>("Validation", v.ErrorMessage ?? "")).ToList();
                    return ServiceResult<bool>.Error(errors);
                }

                if (!setPasswordDTO!.Password!.IsStrongPassword())
                {
                    return ServiceResult<bool>.Error(ErrorsKey.WeakPassword, "Password must be at least 8 characters with uppercase, lowercase, and digit");
                }

                var user = await _userRepository.GetByIdAsync(userId);
                if (user == null || user.Token != codeBase64 || user.IsActive != true || user.IsSysAdmin)
                {
                    return ServiceResult<bool>.Error(ErrorsKey.InvalidInvitation, "Invalid Token");
                }

                if (user.ExpirationTokenDate <= DateTime.UtcNow)
                {
                    return ServiceResult<bool>.Error(ErrorsKey.InvitationExpired, "Expired Token");
                }

                user.SetPassword(setPasswordDTO.ConfirmPassword);
                await _userRepository.UpdateAsync(user);

                return ServiceResult<bool>.Ok(true);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "AuthService.SetNewPassword");
                return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, e.Message);
            }
        }


        private JwtSecurityToken CreateToken(List<Claim> authClaims)
        {
            var secret = _jwtSettings.Secret;
            var authSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

            var issuer = _jwtSettings.ValidIssuer;
            var audience = _jwtSettings.ValidAudience;

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                expires: DateTime.Now.AddMinutes(_jwtSettings.TokenValidityInMinutes),
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
            var secret = _jwtSettings.Secret;
            var tokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
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
