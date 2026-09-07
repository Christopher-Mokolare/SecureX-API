using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SecureX.Api.Data;
using SecureX.Api.Models;

namespace SecureX.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AppDbContext db, ILogger<AuthController> logger) : ControllerBase
{
    [HttpPost("token")]
    public async Task<IActionResult> Token([FromBody] TokenRequest req)
    {
        // Log the request (without password)
        logger.LogInformation("=== TOKEN REQUEST START ===");
        logger.LogInformation("Email: {Email}", req.Email ?? "null");
        logger.LogInformation("HasPassword: {HasPassword}", !string.IsNullOrWhiteSpace(req.Password));
        logger.LogInformation("RequestIP: {IP}", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        logger.LogInformation("UserAgent: {UserAgent}", Request.Headers["User-Agent"].ToString() ?? "unknown");

        // Validate email
        if (string.IsNullOrWhiteSpace(req.Email))
        {
            logger.LogWarning("Token request failed: Email is empty");
            return BadRequest(new ErrorResponse { Error = "Email is required" });
        }

        // Normalize email to lowercase for lookup
        var normalizedEmail = req.Email.ToLowerInvariant();
        logger.LogInformation("Looking up user: {Email}", normalizedEmail);

        // Find user
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);

        if (user == null)
        {
            logger.LogWarning("Token request failed: User not found for email {Email}", normalizedEmail);
            return Unauthorized(new ErrorResponse { Error = "Invalid credentials" });
        }

        logger.LogInformation("User found: Id={Id}, Email={Email}, IsAdmin={IsAdmin}, HasPassword={HasPassword}", 
            user.Id, user.Email, user.IsAdmin, user.PasswordHash != null);

        // Admin accounts require a password
        if (user.IsAdmin)
        {
            logger.LogInformation("Admin login attempt for: {Email}", normalizedEmail);

            // Check if password is provided
            if (string.IsNullOrWhiteSpace(req.Password))
            {
                logger.LogWarning("Admin login failed: No password provided for {Email}", normalizedEmail);
                return Unauthorized(new ErrorResponse { Error = "Password required" });
            }

            // Check if password hash exists
            if (user.PasswordHash == null)
            {
                logger.LogWarning("Admin login failed: No password hash set for {Email}", normalizedEmail);
                return Unauthorized(new ErrorResponse { Error = "Invalid credentials" });
            }

            // Verify password
            bool passwordValid = false;
            try
            {
                passwordValid = BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash);
                logger.LogInformation("Password verification result for {Email}: {Result}", normalizedEmail, passwordValid);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error verifying password for {Email}", normalizedEmail);
                return StatusCode(500, new ErrorResponse { Error = "Internal server error" });
            }

            if (!passwordValid)
            {
                logger.LogWarning("Admin login failed: Invalid password for {Email}", normalizedEmail);
                return Unauthorized(new ErrorResponse { Error = "Invalid credentials" });
            }

            logger.LogInformation("Admin login successful: {Email}", normalizedEmail);
        }
        else
        {
            logger.LogInformation("Non-admin login attempt for: {Email} - Allowed without password", normalizedEmail);
        }

        // Generate JWT token
        try
        {
            var secret = Environment.GetEnvironmentVariable("JWT_SECRET")
                ?? HttpContext.RequestServices.GetRequiredService<IConfiguration>()["JWT_SECRET"];

            if (string.IsNullOrEmpty(secret))
            {
                logger.LogError("JWT_SECRET is not configured");
                return StatusCode(500, new ErrorResponse { Error = "Server configuration error" });
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User"),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            };

            var token = new JwtSecurityToken(
                claims: claims,
                expires: DateTime.UtcNow.AddHours(24),
                signingCredentials: creds);

            var tokenString = new JwtSecurityTokenHandler().WriteToken(token);

            logger.LogInformation("=== TOKEN REQUEST SUCCESS ===");
            logger.LogInformation("Token generated for: {Email}, Role: {Role}", user.Email, user.IsAdmin ? "Admin" : "User");
            logger.LogInformation("Token expiry: {Expiry}", token.ValidTo);

            return Ok(new { token = tokenString });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating token for {Email}", normalizedEmail);
            return StatusCode(500, new ErrorResponse { Error = "Internal server error" });
        }
    }
}

public class TokenRequest
{
    public string Email { get; set; } = "";
    public string? Password { get; set; }
}
