using AuthDAL.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace AuthServices.Services
{
    public class ServiceBase
    {
        private readonly IHttpContextAccessor _context;

        public ServiceBase(IHttpContextAccessor context)
        {
            _context = context;
        }

        /// <summary>
        /// Devuelve una instancia con los datos del usuario logueado.
        /// </summary>
        /// <returns></returns>
        public AuditUser? GetCurrentUser()
        {

            if (_context == null || _context.HttpContext == null)
            {
                return null;
            }

            var userClaims = _context.HttpContext.User;

            var user = new AuditUser
            {
                Id = userClaims.FindFirst("UserId")?.Value?.Trim() ?? string.Empty,
                Email = userClaims.FindFirst("Email")?.Value?.Trim() ?? string.Empty,
                FirstName = userClaims.FindFirst("FirstName")?.Value?.Trim() ?? string.Empty,
                LastName = userClaims.FindFirst("LastName")?.Value?.Trim() ?? string.Empty
            };

            return string.IsNullOrEmpty(user.Id) ? null : user;
        }
    }
}
