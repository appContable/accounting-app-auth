using Microsoft.Extensions.Logging;
using AccountCore.DTO.Auth.IServices;
using AccountCore.DAL.Auth.Models;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AccountCore.DTO.Auth.Parameters;
using AccountCore.DTO.Auth.Entities.User;
using AccountCore.DTO.Auth.IServices.Result;
using Microsoft.AspNetCore.Http;
using AccountCore.Services.Auth.Errors;

namespace AccountCore.Services.Auth.Services
{
    public class UserService : ServiceBase, IUserService
    {
        private readonly ILogger<UserService> _logger;
        private readonly IMapper _mapper;
        private readonly IConfiguration _configuration;

        public UserService(ILogger<UserService> logger, IMapper mapper, IConfiguration configuration, IHttpContextAccessor context) : base(context)
        {
            _logger = logger;
            _mapper = mapper;
            _configuration = configuration;
        }


        /// <inheritdoc/>
        public async Task<ServiceResult<UserDTO>> Create(UserPostDTO userDto)
        {
            try
            {
                var errors = await ValidDTO(userDto);
                if (errors.Any())
                {
                    return ServiceResult<UserDTO>.Error(errors);
                }

                UserDTO? response = null;
                var token = User.GetPassEncode();

                using (var dbcontext = new AuthContext(_configuration))
                {
                    var user = new User
                    {
                        FirstName = userDto.FirstName ?? string.Empty,
                        LastName = userDto.LastName ?? string.Empty,
                        Email = userDto.Email,
                        CreationDate = DateTime.UtcNow,
                        Id = Guid.NewGuid().ToString(),
                        Token = token,
                        ExpirationTokenDate = DateTime.UtcNow.AddDays(2),
                        IsActive = true,
                        IsLock = false,
                        IsSysAdmin = false,
                    };

                    var roles = await dbcontext.Roles.AsQueryable().Where(r => r.IsEnabled && userDto.RoleIds!.Contains(r.RoleKey)).ToListAsync();

                    foreach (var newRoleId in userDto.RoleIds ?? Array.Empty<string>())
                    {
                        var newRole = roles.FirstOrDefault(r => r.RoleKey == newRoleId);
                        if (newRole == null)
                        {
                            return ServiceResult<UserDTO>.Error(Errors.ErrorsKey.Argument, "Invalid Role");
                        }

                        user.AddRole(newRole);
                    }

                    dbcontext.Users.Add(user);

                    await dbcontext.SaveChangesAsync();

                    response = _mapper.Map<UserDTO>(user);
                }

                var urlBase = _configuration["UiUrlBase"];
                var link = $"{urlBase}set-new-password?UserId={response?.Id}&code={token}";

                // var mail = _emailService.Send(userDto.Email, "Bienvenido", link);

                return ServiceResult<UserDTO>.Ok(response!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Create");
                return ServiceResult<UserDTO>.Error(Errors.ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<IEnumerable<UserDTO>>> Find(string? search)
        {
            try
            {
                using (var dbcontext = new AuthContext(_configuration))
                {
                    var parameters = UserParameterDTO.LoadParameters(search);

                    var query = dbcontext.Users.AsQueryable().Where(u => !u.IsSysAdmin);

                    foreach (var paramater in parameters)
                    {
                        if (!string.IsNullOrEmpty(paramater.Name))
                        {
                            query = query.Where(u => u.FirstName.ToLower().Contains(paramater.Name.ToLower()) || u.LastName.ToLower().Contains(paramater.Name.ToLower()));
                        }

                        if (!string.IsNullOrEmpty(paramater.Email))
                        {
                            query = query.Where(u => u.Email.ToLower().Contains(paramater.Email.ToLower()));
                        }
                    }

                    query = query.OrderBy(u => u.LastName);

                    var response = await query.ToListAsync();

                    return ServiceResult<IEnumerable<UserDTO>>.Ok(_mapper.Map<IEnumerable<UserDTO>>(response));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Find");
                return ServiceResult<IEnumerable<UserDTO>>.Error(Errors.ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<UserDTO>> GetById(string id)
        {
            try
            {
                using (var dbcontext = new AuthContext(_configuration))
                {
                    var response = await dbcontext.Users.AsQueryable().Where(t => t.Id == id).FirstOrDefaultAsync();

                    return ServiceResult<UserDTO>.Ok(_mapper.Map<UserDTO>(response));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.GetById");
                return ServiceResult<UserDTO>.Error(Errors.ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<UserDTO>> Update(string userId, UserPostDTO userDto)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return ServiceResult<UserDTO>.Error(Errors.ErrorsKey.Argument, "User Id");
                }

                UserDTO response;
                var errors = await ValidDTO(userDto, userId);

                if (errors.Any())
                {
                    return ServiceResult<UserDTO>.Error(errors);
                }

                using (var dbcontext = new AuthContext(_configuration))
                {
                    var user = await dbcontext.Users.FirstAsync(u => u.Id.Equals(userId) && !u.IsSysAdmin);

                    user.FirstName = userDto.FirstName!;
                    user.LastName = userDto.LastName!;
                    user.Email = userDto.Email;

                    var roles = await dbcontext.Roles.AsQueryable().Where(r => r.IsEnabled && userDto.RoleIds!.Contains(r.Id)).ToListAsync();

                    foreach( var r in user.Roles ?? new List<RoleUser>())
                    {
                        r.Enable = false;
                    }

                    foreach (var newRoleId in userDto.RoleIds ?? Array.Empty<string>())
                    {
                        var newRole = roles.FirstOrDefault(r => r.Id == newRoleId);
                        if (newRole == null)
                        {
                            return ServiceResult<UserDTO>.Error(Errors.ErrorsKey.Argument, "Invalid Role");
                        }

                        user.AddRole(newRole);
                    }

                    dbcontext.Users.Update(user);

                    await dbcontext.SaveChangesAsync();

                    response = _mapper.Map<UserDTO>(user);
                }

                return ServiceResult<UserDTO>.Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Create");
                return ServiceResult<UserDTO>.Error(Errors.ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        /// <inheritdoc/>
        public async Task<ServiceResult<bool>> Delete(string userId)
        {
            try
            {
                if (string.IsNullOrEmpty(userId))
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.Argument, "UserId");
                }

                var userLogged = this.GetCurrentUser();
                using (var dbcontext = new AuthContext(_configuration))
                {
                    var user = await dbcontext.Users.FirstOrDefaultAsync(u => u.Id.Equals(userId) && !u.IsSysAdmin);

                    // Si es usuario de sistema no se puede eliminar
                    if (user == null)
                    {
                        return ServiceResult<bool>.Error(Errors.ErrorsKey.UserNotExist, "Invalid User");
                    }

                    // Si es usuario de sistema no se puede eliminar
                    if (user.Id == userLogged?.Id)
                    {
                        return ServiceResult<bool>.Error(Errors.ErrorsKey.Forbbinden);
                    }

                    dbcontext.Users.Remove(user);
                    await dbcontext.SaveChangesAsync();
                }


                return ServiceResult<bool>.Ok(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserService.Delete");
                return ServiceResult<bool>.Error(Errors.ErrorsKey.InternalErrorCode, ex.Message);
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
                return ServiceResult<bool>.Error(Errors.ErrorsKey.InternalErrorCode, ex.Message);
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
                return ServiceResult<bool>.Error(Errors.ErrorsKey.InternalErrorCode, ex.Message);
            }
        }

        private async Task<ServiceResult<bool>> EnabledDisableUser(string userId, bool active)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return ServiceResult<bool>.Error(Errors.ErrorsKey.Argument, "UserId");
            }

            var userLogged = this.GetCurrentUser();
            using (var dbcontext = new AuthContext(_configuration))
            {
                var user = await dbcontext.Users.FirstOrDefaultAsync(u => u.Id.Equals(userId) && !u.IsSysAdmin);

                // Si es usuario de sistema no se puede eliminar
                if (user == null)
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.UserNotExist, "Invalid User");
                }

                // Si es usuario de sistema no se puede eliminar
                if (user.Id == userLogged?.Id)
                {
                    return ServiceResult<bool>.Error(Errors.ErrorsKey.Forbbinden);
                }

                user.IsActive = active;

                dbcontext.Users.Update(user);
                await dbcontext.SaveChangesAsync();
            }

            return ServiceResult<bool>.Ok(true);
        }

        private async Task<List<KeyValuePair<string, string>>> ValidDTO(UserPostDTO userDto, string? userId = null)
        {
            var errors = new List<KeyValuePair<string, string>>();

            if (userDto == null)
            {
                errors.Add(new KeyValuePair<string, string>(Errors.ErrorsKey.Argument.ToString(), "userDto"));
                return errors;
            }

            if (string.IsNullOrEmpty(userDto.FirstName))
            {
                errors.Add(new KeyValuePair<string, string>(Errors.ErrorsKey.Argument.ToString(), "FirstName"));
            }

            if (string.IsNullOrEmpty(userDto.LastName))
            {
                errors.Add(new KeyValuePair<string, string>(Errors.ErrorsKey.Argument.ToString(), "LastName"));
            }

            if (string.IsNullOrEmpty(userDto.Email))
            {
                errors.Add(new KeyValuePair<string, string>(Errors.ErrorsKey.Argument.ToString(), "Email"));
            }

            if (!IsValidEmail(userDto.Email))
            {
                errors.Add(new KeyValuePair<string, string>(Errors.ErrorsKey.Argument.ToString(), "Email"));
            }

            if (userDto.RoleIds == null || !userDto.RoleIds.Any())
            {
                errors.Add(new KeyValuePair<string, string>(Errors.ErrorsKey.Argument.ToString(), "Role"));
            }

            using (var dbcontext = new AuthContext(_configuration))
            {
                var user = await dbcontext.Users.AsQueryable().Where(user => user.Email.Equals(userDto.Email)).FirstOrDefaultAsync();

                if (user != null)
                {
                    if (user.Id != userId)
                    {
                        errors.Add(new KeyValuePair<string, string>(Errors.ErrorsKey.EmailExists.ToString(), "Email"));
                    }
                }
            }
            return errors;
        }

        static bool IsValidEmail(string email)
        {
            var trimmedEmail = email.Trim();

            if (trimmedEmail.EndsWith("."))
            {
                return false;
            }
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == trimmedEmail;
            }
            catch
            {
                return false;
            }
        }
    }
}
