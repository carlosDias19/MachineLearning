using EstudaBot.Domain.Chatbot;
using Microsoft.EntityFrameworkCore;

namespace EstudaBot.Data;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<ChatInteraction> Interactions => Set<ChatInteraction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var interaction = modelBuilder.Entity<ChatInteraction>();
        interaction.Property(item => item.Question).HasMaxLength(1000).IsRequired();
        interaction.Property(item => item.Answer).HasMaxLength(12000).IsRequired();
        interaction.Property(item => item.Intent).HasMaxLength(80).IsRequired();
        interaction.Property(item => item.SourcesJson).HasMaxLength(16000).IsRequired();
        interaction.HasIndex(item => item.CreatedAtUtc);
        interaction.HasIndex(item => new { item.ApprovedForTraining, item.CreatedAtUtc });
    }
}
