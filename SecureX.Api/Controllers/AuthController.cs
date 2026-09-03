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
public class AuthController(AppDbContext db) : ControllerBase
{
    [HttpPost("token")]
    public async Task<IActionResult> Token([FromBody] TokenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new ErrorResponse { Error = "Email is required" });

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.ToLowerInvariant());

        // Admin accounts require a password
        if (user?.IsAdmin == true)
        {
            if (string.IsNullOrWhiteSpace(req.Password))
                return Unauthorized(new ErrorResponse { Error = "Password required" });
            if (user.PasswordHash is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Unauthorized(new ErrorResponse { Error = "Invalid credentials" });
        }

        var secret = Environment.GetEnvironmentVariable("JWT_SECRET")
            ?? HttpContext.RequestServices.GetRequiredService<IConfiguration>()["JWT_SECRET"]!;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, req.Email),
            new(ClaimTypes.Role, user?.IsAdmin == true ? "Admin" : "User"),
        };

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds);

        return Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }
}

public class TokenRequest
{
    public string Email { get; set; } = "";
    public string? Password { get; set; }
}
