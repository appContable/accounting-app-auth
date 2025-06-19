namespace AuthDTO.Entities
{
    public class EmailTemplateDTO
    {
        public int Id { get; set; }
        public string TemplateKey { get; set; } = null!;
        public string? TemplateText { get; set; }
        public string? TemplateHtml { get; set; }
        public string Subject { get; set; } = null!;
    }
}
