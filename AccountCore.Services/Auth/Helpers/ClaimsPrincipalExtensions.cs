using System.Security.Claims;

public static class ClaimsPrincipalExtensions
{
    public const string AdminRole = "admin";
    public const string OperationRole = "operations";
    public const string MonitorRole = "monitor";
    public const string UserId = "UserId";
    public const string Email = "Email";
    public const string FirstName = "FirstName";
    public const string LastName = "LastName";
    public const string Cuit = "Cuit";

    internal static string GetEmail(this ClaimsPrincipal claimsPrincipal)
        => claimsPrincipal.FindFirst("Email")?.Value?.Trim() ?? string.Empty;

    internal static string GetFirstName(this ClaimsPrincipal claimsPrincipal)
        => claimsPrincipal.FindFirst("Name")?.Value?.Trim() ?? string.Empty;

    internal static string GetLastName(this ClaimsPrincipal claimsPrincipal)
        => claimsPrincipal.FindFirst("Surname")?.Value?.Trim() ?? string.Empty;

}