using Microsoft.EntityFrameworkCore;

namespace ClaimBackend.Api.Data;

public class ClaimBackendDbContext(DbContextOptions<ClaimBackendDbContext> options) : DbContext(options)
{
}
