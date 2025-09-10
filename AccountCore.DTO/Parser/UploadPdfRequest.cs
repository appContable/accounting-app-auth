using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AccountCore.DTO.Parser
{
    public class UploadPdfRequest
    {
        [FromForm(Name = "bank")]
        public string? Bank { get; set; }

        [Required]
        [FromForm(Name = "file")]
        public IFormFile File { get; set; } = default!;
    }
}
