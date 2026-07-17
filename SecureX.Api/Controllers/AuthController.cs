using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using SecureX.Api.Models;

namespace SecureX.Api.Controllers;

/// <summary>
/// Temporary dev token endpoint — replace with Cognito when ready.
/// POST /api/auth/token  { "email": "user@example.com" }
/// Returns a 24-hour JWT signed with JWT_SECRET.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    [HttpPost("token")]
    public IActionResult Token([FromBody] TokenRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new ErrorResponse { Error = "Email is required" });

        var secret = Environment.GetEnvironmentVariable("JWT_SECRET")
            ?? HttpContext.RequestServices.GetRequiredService<IConfiguration>()["JWT_SECRET"]!;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: [new Claim(ClaimTypes.Email, req.Email)],
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds);

        return Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }
}

public class TokenRequest
{
    public string Email { get; set; } = "";
}
