using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UnifiedInbox.Application;
using UnifiedInbox.Domain;

namespace UnifiedInbox.Api.Security;

public sealed class JwtTokenIssuer(IConfiguration configuration) : ITokenIssuer
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(User user)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt:SigningKey is required."))), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(configuration["Jwt:Issuer"], configuration["Jwt:Audience"],
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("tenant_id", user.TenantId.ToString()), new Claim(ClaimTypes.Role, user.Role.ToString()), new Claim(JwtRegisteredClaimNames.Email, user.Email)],
            expires: expires.UtcDateTime, signingCredentials: credentials);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
