using EstudaBot.Domain.Chatbot;
using Microsoft.EntityFrameworkCore;

namespace EstudaBot.Data;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<ChatInteraction> Interactions => Set<ChatInteraction>();
}
