using Microsoft.Extensions.Logging;
using AccountCore.DTO.Auth.IServices;
using AccountCore.DAL.Auth.Models;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using AccountCore.DTO.Auth.Parameters;
using AccountCore.DTO.Auth.Entities.User;
using AccountCore.DTO.Auth.IServices.Result;
using Microsoft.AspNetCore.Http;
using AccountCore.Services.Auth.Errors;
using AccountCore.Services.Auth.Interfaces;
using AccountCore.DTO.Auth.Validation;
using AccountCore.DTO.Auth.Configuration;
using Microsoft.Extensions.Options;
using System.Text;

namespace AccountCore.Services.Auth.Services
{
    public class UserService : ServiceBase, IUserService
    {
        private readonly ILogger<UserService> _logger;
        private readonly IMapper _mapper;
        private readonly IUserRepository _userRepository;
        private readonly IRoleRepository _roleRepository;
        private readonly IEmailService _emailService;
        private readonly EmailSettings _emailSettings;

        public UserService(
            ILogger<UserService> logger,
            IMapper mapper,
            IUserRepository userRepository,
            IRoleRepository roleRepository,
            IEmailService emailService,
            IOptions<EmailSettings> emailOptions,
            IHttpContextAccessor context) : base(context)
        {
            _logger = logger;
            _mapper = mapper;
            _userRepository = userRepository;
            _roleRepository = roleRepository;
            _emailService = emailService;
            _emailSettings = emailOptions.Value;
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<UserDTO>> Create(UserPostDTO userDto)
        {
            try
            {
                var validationResults = ValidationExtensions.ValidateObject(userDto);
                if (validationResults.Any())
                {
                    var validationErrors = validationResults.Select(v => new KeyValuePair<string, string>("Validation", v.ErrorMessage ?? "")).ToList();
                    return ServiceResult<UserDTO>.Error(validationErrors);
                }

                var businessErrors = await ValidateUserDto(userDto);
                if (businessErrors.Any())
                {
                    return ServiceResult<UserDTO>.Error(businessErrors);
                }

                var token = User.GetPassEncode();

                var user = new User
                {
                    FirstName = userDto.FirstName,
                    LastName = userDto.LastName,
                    Cuit = userDto.Cuit,
                    Email = userDto.Email,
                    CreationDate = DateTime.UtcNow,
                    Id = Guid.NewGuid().ToString(),
                    Token = token,
                    ExpirationTokenDate = DateTime.UtcNow.AddDays(2),
                    IsActive = true,
                    IsLock = false,
                    IsSysAdmin = false,
                };

                var roles = await _roleRepository.GetRolesByIdsAsync(userDto.RoleIds ?? Array.Empty<string>());

                foreach (var newRoleId in userDto.RoleIds ?? Array.Empty<string>())
                {
                    var newRole = roles.FirstOrDefault(r => r.RoleKey == newRoleId);
                    if (newRole == null)
                    {
                        return ServiceResult<UserDTO>.Error(ErrorsKey.Argument, $"Invalid Roles: {newRoleId}");
                    }

                    user.AddRole(newRole);
                }

                var createdUser = await _userRepository.CreateAsync(user);
                var response = _mapper.Map<UserDTO>(createdUser);

                var uiBaseUrl = _emailSettings.UiBaseUrl?.TrimEnd('/') ?? string.Empty;
                if (string.IsNullOrEmpty(uiBaseUrl))
                {
                    return ServiceResult<UserDTO>.Error(ErrorsKey.InternalErrorCode, "UiBaseUrl is not configured");
                }

                var tokenBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
                var link = $"{uiBaseUrl}/set-password/{response.Id}/{tokenBase64}";
                var welcomeEmailResult = await _emailService.SendWelcomeEmailAsync(createdUser, link);
                if (!welcomeEmailResult.Success)
                {
                    return ServiceResult<UserDTO>.Error(welcomeEmailResult.Errors ??
                        new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(ErrorsKey.InternalErrorCode.ToString(), "Unable to send welcome email") });
                }

                return ServiceResult<UserDTO>.Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Create");
                return ServiceResult<UserDTO>.Error(ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<IEnumerable<UserDTO>>> Find(string? search)
        {
            try
            {
                var users = await _userRepository.FindAsync(search);
                var response = _mapper.Map<IEnumerable<UserDTO>>(users);
                return ServiceResult<IEnumerable<UserDTO>>.Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Find");
                return ServiceResult<IEnumerable<UserDTO>>.Error(ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<UserDTO>> GetById(string id)
        {
            try
            {
                var user = await _userRepository.GetByIdAsync(id);
                var response = _mapper.Map<UserDTO>(user);
                return ServiceResult<UserDTO>.Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.GetById");
                return ServiceResult<UserDTO>.Error(ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<UserDTO>> Update(string userId, UserPostDTO userDto)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return ServiceResult<UserDTO>.Error(ErrorsKey.Argument, "User Id");
                }

                var validationResults = ValidationExtensions.ValidateObject(userDto);
                if (validationResults.Any())
                {
                    var validationErrors = validationResults.Select(v => new KeyValuePair<string, string>("Validation", v.ErrorMessage ?? "")).ToList();
                    return ServiceResult<UserDTO>.Error(validationErrors);
                }

                var businessErrors = await ValidateUserDto(userDto, userId);
                if (businessErrors.Any())
                {
                    return ServiceResult<UserDTO>.Error(businessErrors);
                }

                var user = await _userRepository.GetByIdAsync(userId);
                if (user == null || user.IsSysAdmin)
                {
                    return ServiceResult<UserDTO>.Error(ErrorsKey.UserNotExist, "User not found");
                }

                user.FirstName = userDto.FirstName;
                user.LastName = userDto.LastName;
                user.Cuit = userDto.Cuit;
                user.Email = userDto.Email;

                var roles = await _roleRepository.GetRolesByIdsAsync(userDto.RoleIds ?? Array.Empty<string>());

                // Disable existing roles
                var existingRoles = user.Roles ?? Enumerable.Empty<RoleUser>();
                foreach (var r in existingRoles)
                {
                    r.Enable = false;
                }

                // Add new roles
                foreach (var newRoleId in userDto.RoleIds ?? Array.Empty<string>())
                {
                    var newRole = roles.FirstOrDefault(r => r.Id == newRoleId);
                    if (newRole == null)
                    {
                        return ServiceResult<UserDTO>.Error(ErrorsKey.Argument, "Invalid Role");
                    }

                    user.AddRole(newRole);
                }

                var updatedUser = await _userRepository.UpdateAsync(user);
                var response = _mapper.Map<UserDTO>(updatedUser);

                return ServiceResult<UserDTO>.Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Update");
                return ServiceResult<UserDTO>.Error(ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<bool>> Delete(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return ServiceResult<bool>.Error(ErrorsKey.Argument, "UserId");
                }

                var userLogged = this.GetCurrentUser();
                var user = await _userRepository.GetByIdAsync(userId);

                if (user == null || user.IsSysAdmin)
                {
                    return ServiceResult<bool>.Error(ErrorsKey.UserNotExist, "Invalid User");
                }

                if (user.Id == userLogged?.Id)
                {
                    return ServiceResult<bool>.Error(ErrorsKey.Forbbinden, "Cannot delete your own account");
                }

                await _userRepository.DeleteAsync(userId);

                return ServiceResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Delete");
                return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<bool>> Enable(string userId)
        {
            try
            {
                return await EnabledDisableUser(userId, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Enable");
                return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<bool>> Disable(string userId)
        {
            try
            {
                return await EnabledDisableUser(userId, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Disable");
                return ServiceResult<bool>.Error(ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        private async Task<ServiceResult<bool>> EnabledDisableUser(string userId, bool active)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return ServiceResult<bool>.Error(ErrorsKey.Argument, "UserId");
            }

            var userLogged = this.GetCurrentUser();
            var user = await _userRepository.GetByIdAsync(userId);

            if (user == null || user.IsSysAdmin)
            {
                return ServiceResult<bool>.Error(ErrorsKey.UserNotExist, "Invalid User");
            }

            if (user.Id == userLogged?.Id)
            {
                return ServiceResult<bool>.Error(ErrorsKey.Forbbinden, "Cannot modify your own account status");
            }

            user.IsActive = active;
            await _userRepository.UpdateAsync(user);

            return ServiceResult<bool>.Ok(true);
        }

        private async Task<List<KeyValuePair<string, string>>> ValidateUserDto(UserPostDTO userDto, string? userId = null)
        {
            var errors = new List<KeyValuePair<string, string>>();

            if (!userDto.Email.IsValidEmail())
            {
                errors.Add(new KeyValuePair<string, string>(ErrorsKey.Argument.ToString(), "Invalid email format"));
            }

            if (await _userRepository.EmailExistsAsync(userDto.Email, userId))
            {
                errors.Add(new KeyValuePair<string, string>(ErrorsKey.EmailExists.ToString(), "Email already exists"));
            }

            if (await _userRepository.CuitExistsAsync(userDto.Cuit, userId))
            {
                errors.Add(new KeyValuePair<string, string>(ErrorsKey.Argument.ToString(), "CUIT already exists"));
            }

            var validRoles = await _roleRepository.GetEnabledRolesAsync();
            var validRoleIds = validRoles.Select(r => r.RoleKey).ToHashSet();
            
            foreach (var roleId in userDto.RoleIds ?? Array.Empty<string>())
            {
                if (!validRoleIds.Contains(roleId))
                {
                    errors.Add(new KeyValuePair<string, string>(ErrorsKey.InvalidRol.ToString(), $"Invalid roleamd: {roleId}"));
                }
            }

            return errors;
        }
    }
}