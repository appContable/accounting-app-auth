using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AccountCore.DAL.Parser.Models;              // UserCategoryRule
using AccountCore.Services.Parser.Interfaces;     // IUserCategoryRuleRepository, ICategorizationService

namespace AccountCore_API.Controllers
{
    /// <summary>
    /// Gestión de reglas de categorización de usuario (por banco).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class RulesController : ControllerBase
    {
        private readonly IUserCategoryRuleRepository _userRepo;
        private readonly ICategorizationService _categorizationService;

        public RulesController(
            IUserCategoryRuleRepository userRepo,
            ICategorizationService categorizationService)
        {
            _userRepo = userRepo;
            _categorizationService = categorizationService;
        }

        /// <summary>
        /// Lista las reglas del usuario para un banco. Podés filtrar por solo activas.
        /// </summary>
        /// <param name="userId">Identificador del usuario.</param>
        /// <param name="bank">Banco al que aplican las reglas.</param>
        /// <param name="onlyActive">Si true, solo devuelve reglas activas.</param>
        [HttpGet]
        [SwaggerOperation(Summary = "Lista reglas del usuario para un banco")]
        [SwaggerResponse(StatusCodes.Status200OK, "Listado de reglas")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Parámetros inválidos")]
        public async Task<IActionResult> Get(
            [FromQuery] string userId,
            [FromQuery] string bank,
            [FromQuery] bool onlyActive = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(bank))
                return BadRequest("userId y bank son requeridos.");

            var rules = await _userRepo.GetByUserAndBankAsync(userId, bank, ct);
            if (onlyActive) rules = rules.Where(r => r.Active).ToList();

            return Ok(rules);
        }

        /// <summary>
        /// Aprende/actualiza (upsert) una regla de categorización para el usuario.
        /// Centraliza la lógica en ICategorizationService.LearnAsync.
        /// </summary>
        [HttpPost("learn")]
        [SwaggerOperation(Summary = "Aprende/actualiza una regla de categorización del usuario")]
        [SwaggerResponse(StatusCodes.Status200OK, "Regla guardada")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Datos inválidos")]
        public async Task<IActionResult> Learn(
            [FromBody] AccountCore.DTO.Parser.Parameters.LearnRuleRequest body,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(body.UserId) ||
                string.IsNullOrWhiteSpace(body.Bank) ||
                string.IsNullOrWhiteSpace(body.Pattern) ||
                string.IsNullOrWhiteSpace(body.Category))
            {
                return BadRequest("Campos requeridos: userId, bank, pattern, category.");
            }

            var saved = await _categorizationService.LearnAsync(body, ct);
            return Ok(saved);
        }

        /// <summary>
        /// Desactiva una regla del usuario (soft delete). Como el repo no tiene Deactivate,
        /// se hace GetByUserAndBank + set Active=false + Upsert.
        /// </summary>
        [HttpPatch("{id:guid}/deactivate")]
        [SwaggerOperation(Summary = "Desactiva una regla de usuario")]
        [SwaggerResponse(StatusCodes.Status204NoContent, "Regla desactivada")]
        [SwaggerResponse(StatusCodes.Status400BadRequest, "Parámetros inválidos")]
        [SwaggerResponse(StatusCodes.Status404NotFound, "Regla no encontrada")]
        public async Task<IActionResult> Deactivate(
            Guid id,
            [FromQuery] string userId,
            [FromQuery] string bank,
            CancellationToken ct = default)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(bank))
                return BadRequest("id, userId y bank son requeridos.");

            var rules = await _userRepo.GetByUserAndBankAsync(userId, bank, ct);
            var rule = rules.FirstOrDefault(r => r.Id == id);
            if (rule is null) return NotFound();

            rule.Active = false;
            rule.UpdatedAt = DateTime.UtcNow;

            await _userRepo.UpsertAsync(rule, ct);
            return NoContent();
        }
    }
}
