using AccountCore.DTO.Auth.IServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AccountCore.DTO.Auth.Entities.User;
using Swashbuckle.AspNetCore.Annotations;
using AccountCore.Services.Auth.Errors;
using AccountCore.DTO.Auth.Validation;


namespace AccountCore.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        public readonly IUserService _userService;
        private readonly ILogger<UserController> _logger;

        public UserController(IUserService userService, ILogger<UserController> logger)
        {
            _userService = userService;
            _logger = logger;
        }

        [HttpGet]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{ClaimsPrincipalExtensions.AdminRole}")]
        [SwaggerOperation(Summary = "Retrieve a user by id")]
        [SwaggerResponse(StatusCodes.Status200OK, "User found", typeof(UserDTO))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid request")]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token not valid or expired")]
        [SwaggerResponse(StatusCodes.Status403Forbidden, "Insufficient permissions - admin role required")]
        [SwaggerResponse(StatusCodes.Status404NotFound, "User not found")]
        public async Task<IActionResult> Get(string id)
        {
            var response = await _userService.GetById(id);

            if (response.Success)
            {
                return response.Value == null ? NotFound() : Ok(response.Value);
            }

            return BadRequest(response.Errors);
        }


        [HttpGet]
        [Route("Find")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{ClaimsPrincipalExtensions.AdminRole}")]
        [SwaggerOperation(Summary = "Find users matching the search value")]
        [SwaggerResponse(StatusCodes.Status200OK, "Users found", typeof(IEnumerable<UserDTO>))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid request")]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token not valid or expired")]
        [SwaggerResponse(StatusCodes.Status403Forbidden, "Insufficient permissions - admin role required")]
        public async Task<IActionResult> Find(string? value)
        {
            var response = await _userService.Find(value);

            if (response.Success)
            {
                return Ok(response.Value);
            }

            return BadRequest(response.Errors);
        }


        [HttpPost]
        [AllowAnonymous]
        [SwaggerOperation(Summary = "Create a new user")]
        [SwaggerResponse(StatusCodes.Status200OK, "User created successfully", typeof(UserDTO))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid user data or validation errors")]
        public async Task<IActionResult> Post([FromBody] UserPostDTO userDTO)
        {
            var validationResults = ValidationExtensions.ValidateObject(userDTO);
            if (validationResults.Any())
            {
                var errors = validationResults.Select(v => v.ErrorMessage).ToList();
                return BadRequest(errors);
            }

            var response = await _userService.Create(userDTO);

            if (response.Success)
            {
                return Ok(response.Value);
            }

            return BadRequest(response.Errors);
        }

        [HttpPut]
        [Route("{userId}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{ClaimsPrincipalExtensions.AdminRole}")]
        [SwaggerOperation(Summary = "Update an existing user")]
        [SwaggerResponse(StatusCodes.Status200OK, "User updated successfully", typeof(UserDTO))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid user data or validation errors")]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token not valid or expired")]
        [SwaggerResponse(StatusCodes.Status403Forbidden, "Insufficient permissions - admin role required")]
        public async Task<IActionResult> Put(string userId, [FromBody] UserPostDTO userDto)
        {
            var validationResults = ValidationExtensions.ValidateObject(userDto);
            if (validationResults.Any())
            {
                var errors = validationResults.Select(v => v.ErrorMessage).ToList();
                return BadRequest(errors);
            }

            var response = await _userService.Update(userId, userDto);

            if (response.Success)
            {
                return Ok(response.Value);
            }

            return BadRequest(response.Errors);
        }

        [HttpDelete]
        [Route("{userId}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{ClaimsPrincipalExtensions.AdminRole}")]
        [SwaggerOperation(Summary = "Delete a user by id")]
        [SwaggerResponse(StatusCodes.Status200OK, "User deleted successfully")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid request")]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token not valid or expired")]
        [SwaggerResponse(StatusCodes.Status403Forbidden, "Insufficient permissions - admin role required")]
        public async Task<IActionResult> Delete(string userId)
        {
            var response = await _userService.Delete(userId);

            if (response.Success)
            {
                return Ok();
            }

            return BadRequest(response.Errors);
        }

        [HttpPatch]
        [Route("Enable/{userId}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{ClaimsPrincipalExtensions.AdminRole}")]
        [SwaggerOperation(Summary = "Enable a user account")]
        [SwaggerResponse(StatusCodes.Status200OK, "User account enabled successfully")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid request")]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token not valid or expired")]
        [SwaggerResponse(StatusCodes.Status403Forbidden, "Insufficient permissions - admin role required")]
        public async Task<IActionResult> Enable(string userId)
        {
            var response = await _userService.Enable(userId);

            if (response.Success)
            {
                return Ok();
            }

            return BadRequest(response.Errors);
        }

        [HttpPatch]
        [Route("Disable/{userId}")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = $"{ClaimsPrincipalExtensions.AdminRole}")]
        [SwaggerOperation(Summary = "Disable a user account")]
        [SwaggerResponse(StatusCodes.Status200OK, "User account disabled successfully")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid request")]
        [SwaggerResponse(StatusCodes.Status401Unauthorized, "Token not valid or expired")]
        [SwaggerResponse(StatusCodes.Status403Forbidden, "Insufficient permissions - admin role required")]
        public async Task<IActionResult> Disable(string userId)
        {
            var response = await _userService.Disable(userId);

            if (response.Success)
            {
                return Ok();
            }

            return BadRequest(response.Errors);
        }
    }
}
