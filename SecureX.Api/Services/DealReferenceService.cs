using Microsoft.EntityFrameworkCore;
using Npgsql;
using SecureX.Api.Data;

namespace SecureX.Api.Services;

public class DealReferenceService(AppDbContext db)
{
    public async Task<string> NextAsync()
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT nextval('deal_reference_seq')";
        var seq = (long)(await cmd.ExecuteScalarAsync())!;
        return $"SX-{DateTime.UtcNow.Year}-{seq:D6}";
    }
}
