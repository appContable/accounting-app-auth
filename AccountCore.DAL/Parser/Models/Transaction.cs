using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace AccountCore.DAL.Parser.Models
{
    public class Transaction
    {
        public DateTime Date { get; set; } // Lo que muestra la UI (ya “limpio”/canónico)
        public string Description { get; set; } = ""; // Lo que salió del PDF tal cual (sirve para debug)
        public string? OriginalDescription { get; set; } // Descripción original del PDF (sin limpieza, para debug)
        public decimal Amount { get; set; } // signo natural del movimiento
        public string Type { get; set; } = "debit"; // "debit" | "credit" (derivado de Amount)
        public decimal Balance { get; set; } // saldo luego del movimiento

        public string? Category { get; set; }
        public string? Subcategory { get; set; }

        /// <summary>"bank-rule" | "user-rule" | "user-learned"</summary>
        public string? CategorySource { get; set; }

        /// <summary>Id de la regla aplicada si corresponde</summary>
        [BsonGuidRepresentation(GuidRepresentation.CSharpLegacy)]
        public Guid? CategoryRuleId { get; set; }
        
        
    }
}
