using AccountCore.API.Auth;
using AccountCore.DTO.Auth.Entities;
using AccountCore.DTO.Auth.Entities.User;
using AccountCore.DTO.Auth.IServices;
using AccountCore.DTO.Auth.ReturnsModels;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Swashbuckle.AspNetCore.Annotations;

namespace AccountCore.API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IConfiguration _configuration;

        public AuthController(IAuthService authService, IConfiguration configuration)
        {
            _authService = authService;
            _configuration = configuration;
        }

        [AllowAnonymous]
        [HttpPost("authentication")]
        [SwaggerOperation(Summary = "Authenticate user and generate JWT token")]
        public async Task<IActionResult> Authentication([FromBody] AuthenticationDTO user)
        {
            var token = await _authService.Authentication(user.Email, user.Password);

            if (token.Success)
            {
                return Ok(token.Value);
            }

            return BadRequest(token.Errors);
        }

        [HttpPost("SetNewPassword/{userId}/{codeBase64}")]
        [SwaggerOperation(Summary = "Confirm password reset with verification code")]
        public async Task<IActionResult> SetNewPassword(string userId, string codeBase64, SetPasswordDTO setPasswordDTO)
        {
            var token = await _authService.SetNewPassword(userId, codeBase64, setPasswordDTO);

            if (token.Success)
            {
                return Ok(true);
            }

            return BadRequest(token.Errors);
        }

        [HttpPost("ResetPassword")]
        [SwaggerOperation(Summary = "Send password reset instructions to email")]
        public async Task<IActionResult> ResetPassword([FromForm] string email)
        {
            var token = await _authService.ResetPassword(email);

            if (token.Success)
            {
                return Ok(true);
            }

            return BadRequest(token.Errors);
        }

        [HttpPost]
        [Route("refresh-token")]
        [SwaggerOperation(Summary = "Refresh JWT using a valid refresh token")]
        public async Task<IActionResult> RefreshToken(TokenModelDTO tokenModel)
        {
            if (tokenModel is null)
            {
                return BadRequest("Invalid client request");
            }

            var result = await _authService.RefreshToken(tokenModel);

            if (!result.Success)
            {
                return BadRequest(result.Errors);
            }

            return Ok(result.Value);
        }
    }
}
