using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;

namespace AccountCore.API.Configuration
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddValidatedConfiguration<T>(
            this IServiceCollection services,
            IConfiguration configuration,
            string sectionName) where T : class, new()
        {
            var section = configuration.GetSection(sectionName);
            var config = section.Get<T>() ?? new T();
            
            // Validate configuration
            var validationResults = new List<ValidationResult>();
            var context = new ValidationContext(config);
            
            if (!Validator.TryValidateObject(config, context, validationResults, true))
            {
                var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
                throw new InvalidOperationException($"Invalid configuration for {sectionName}: {errors}");
            }
            
            services.Configure<T>(section);
            return services;
        }
    }
}