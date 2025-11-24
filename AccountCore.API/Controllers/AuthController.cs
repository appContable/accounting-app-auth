using AccountCore.DTO.Auth.Entities;
using AccountCore.DTO.Auth.Entities.User;
using AccountCore.DTO.Auth.IServices;
using AccountCore.DTO.Auth.ReturnsModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using AccountCore.DTO.Auth.Validation;
using Microsoft.AspNetCore.RateLimiting;

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
        [SwaggerResponse(StatusCodes.Status200OK, "Authentication successful", typeof(ReturnTokenDTO))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid credentials or validation errors")]
        public async Task<IActionResult> Authentication([FromBody] AuthenticationDTO user)
        {
            var validationResults = ValidationExtensions.ValidateObject(user);
            if (validationResults.Any())
            {
                var errors = validationResults.Select(v => v.ErrorMessage).ToList();
                return BadRequest(errors);
            }

            var token = await _authService.Authentication(user.Login!, user.Password!);

            if (token.Success)
            {
                return Ok(token.Value);
            }

            return BadRequest(token.Errors);
        }

        [AllowAnonymous]
        [HttpPost("SetNewPassword/{userId}/{codeBase64}")]
        [SwaggerOperation(Summary = "Confirm password reset with verification code")]
        [SwaggerResponse(StatusCodes.Status200OK, "Password reset successfully")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid request or validation errors")]
        public async Task<IActionResult> SetNewPassword(string userId, string codeBase64, SetPasswordDTO setPasswordDTO)
        {
            var validationResults = ValidationExtensions.ValidateObject(setPasswordDTO);
            if (validationResults.Any())
            {
                var errors = validationResults.Select(v => v.ErrorMessage).ToList();
                return BadRequest(errors);
            }

            var token = await _authService.SetNewPassword(userId, codeBase64, setPasswordDTO);

            if (token.Success)
            {
                return Ok(true);
            }

            return BadRequest(token.Errors);
        }

        [AllowAnonymous]
        [HttpPost("ResetPassword")]
        [SwaggerOperation(Summary = "Send password reset instructions to email")]
        [SwaggerResponse(StatusCodes.Status200OK, "Password reset instructions sent")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid email or user not found")]
        public async Task<IActionResult> ResetPassword([FromForm] string email)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.IsValidEmail())
            {
                return BadRequest("Valid email is required");
            }

            var token = await _authService.ResetPassword(email);

            if (token.Success)
            {
                return Ok(true);
            }

            return BadRequest(token.Errors);
        }

        [AllowAnonymous]
        [HttpPost]
        [Route("refresh-token")]
        [SwaggerOperation(Summary = "Refresh JWT using a valid refresh token")]
        [SwaggerResponse(StatusCodes.Status200OK, "Token refreshed successfully", typeof(ReturnTokenDTO))]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Invalid token or refresh token")]
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
