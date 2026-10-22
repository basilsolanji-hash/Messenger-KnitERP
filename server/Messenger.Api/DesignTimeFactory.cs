using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Messenger.Api;

// Migration generation does not need application credentials or an active database.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MessengerDb>
{
    public MessengerDb CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Messenger")
            ?? "Host=localhost;Database=messenger_design;Username=design";
        return new(new DbContextOptionsBuilder<MessengerDb>().UseNpgsql(connection).Options);
    }
}
