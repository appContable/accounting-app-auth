using System.ComponentModel.DataAnnotations;

namespace AccountCore.DTO.Parser.Parameters
{
    /// <summary>
    /// Pedido para aprender/registrar una regla de categorización por USUARIO y BANCO.
    /// Los campos opcionales permiten refinar la regla sin romper compatibilidad.
    /// </summary>
    public class LearnRuleRequest
    {
        [Required]
        [MaxLength(128)]
        public string UserId { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string Bank { get; set; } = string.Empty;

        [Required]
        [MaxLength(512)]
        public string Pattern { get; set; } = string.Empty;

        [Required]
        [MaxLength(128)]
        public string Category { get; set; } = string.Empty;

        // --- Opcionales ---
        [MaxLength(128)]
        public string? Subcategory { get; set; }

        /// <summary>
        /// "Contains" | "Equals" | "StartsWith" | "EndsWith" | "Regex"
        /// Si viene null/empty, se asume "Contains".
        /// </summary>
        public string? PatternType { get; set; }

        /// <summary>
        /// Menor número = mayor prioridad (default 100).
        /// </summary>
        public int? Priority { get; set; }
    }
}
