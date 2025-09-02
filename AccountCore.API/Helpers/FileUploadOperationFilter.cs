using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AccountCore.API.Helpers
{
    public class FileUploadOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            // Caso A: la acción recibe [FromForm] UploadPdfRequest
            var hasUploadPdfRequest = context.MethodInfo
                .GetParameters()
                .Any(p => p.ParameterType.Name == "UploadPdfRequest");

            if (hasUploadPdfRequest)
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content =
                    {
                        ["multipart/form-data"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = new Dictionary<string, OpenApiSchema>
                                {
                                    ["bank"]   = new OpenApiSchema { Type = "string" },
                                    ["file"]   = new OpenApiSchema { Type = "string", Format = "binary" }
                                },
                                Required = new HashSet<string> { "bank", "file" }
                            }
                        }
                    }
                };
                return;
            }

            // Caso B: la acción recibe IFormFile directo -> comportamiento anterior
            var fileParams = context.ApiDescription.ParameterDescriptions
                .Where(p => p.Type == typeof(IFormFile))
                .ToList();

            if (fileParams.Count == 0) return;

            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content =
                {
                    ["multipart/form-data"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Properties = fileParams.ToDictionary(
                                p => p.Name!,
                                p => new OpenApiSchema { Type = "string", Format = "binary" }
                            ),
                            Required = fileParams.Select(p => p.Name!).ToHashSet()
                        }
                    }
                }
            };
        }
    }
}
