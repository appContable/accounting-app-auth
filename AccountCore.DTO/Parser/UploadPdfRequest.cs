using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AccountCore.DTO.Parser
{
    public class UploadPdfRequest
    {
        [Required]
        [FromForm(Name = "bank")]
        public string Bank { get; set; } = string.Empty;

        [Required]
        [FromForm(Name = "file")]
        public IFormFile File { get; set; } = default!;
    }
}
