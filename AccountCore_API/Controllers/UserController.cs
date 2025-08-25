using AccountCore.DTO.Auth.Entities;
using AccountCore.DTO.Auth.Parameters;
using AccountCore.DTO.Auth.IServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using AccountCore.DTO.Auth.Entities.User;

namespace AccountCore_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
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
        public async Task<IActionResult> Post([FromBody] UserPostDTO userDTO)
        {
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
        public async Task<IActionResult> Put(string userId, [FromBody] UserPostDTO userDto)
        {
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
