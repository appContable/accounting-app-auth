using AccountCore.Services.Parser.Interfaces;
using AccountCore.DAL.Parser.Models;
using System.Text.Json;

namespace AccountCore.API.Infraestructure
{
    public class MongoIndexHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        public MongoIndexHostedService(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

        public async Task StartAsync(CancellationToken ct)
        {
            using var scope = _serviceProvider.CreateScope();
            
            // 1. Ensure Indexes
            var ruleRepo = scope.ServiceProvider.GetRequiredService<IUserCategoryRuleRepository>();
            await ruleRepo.EnsureIndexesAsync(ct);

            // 2. Track Version
            await TrackCurrentVersion(scope.ServiceProvider, ct);
        }

        private async Task TrackCurrentVersion(IServiceProvider sp, CancellationToken ct)
        {
            try
            {
                var versionRepo = sp.GetRequiredService<IAppVersionRepository>();
                var config = sp.GetRequiredService<IConfiguration>();
                var env = sp.GetRequiredService<IWebHostEnvironment>();
                
                var currentVersion = config["Api:Version"] ?? "1.0.0";
                
                if (!await versionRepo.ExistsAsync(currentVersion))
                {
                    var appVersion = new AppVersion
                    {
                        Version = currentVersion,
                        ReleaseDate = DateTime.UtcNow,
                        IsCurrent = true
                    };

                    // Try to get changes from changelog.json
                    var changelogPath = Path.Combine(env.ContentRootPath, "changelog.json");
                    if (File.Exists(changelogPath))
                    {
                        var content = await File.ReadAllTextAsync(changelogPath, ct);
                        var logs = JsonSerializer.Deserialize<List<JsonElement>>(content);
                        var currentLog = logs?.FirstOrDefault(x => x.GetProperty("version").GetString() == currentVersion);
                        
                        if (currentLog.HasValue && currentLog.Value.TryGetProperty("changes", out var changesProp))
                        {
                            appVersion.Changes = changesProp.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                        }
                    }

                    await versionRepo.CreateAsync(appVersion);
                }
            }
            catch
            {
                // Do not block startup if version tracking fails
            }
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
