using Microsoft.EntityFrameworkCore;
using Sequfloh.Domain.Messages;
using Sequfloh.Infrastructure.Identity;

namespace Sequfloh.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();

    private static void ConfigureMessages(ModelBuilder b)
    {
        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.Property(x => x.Title).HasMaxLength(120);
            e.Property(x => x.LastMessagePreview).HasMaxLength(160);
            e.HasIndex(x => x.LastMessageAt);
        });
        b.Entity<ConversationMember>(e =>
        {
            e.ToTable("conversation_members");
            e.HasIndex(x => new { x.ConversationId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.Property(x => x.Body).HasMaxLength(4000);
            e.Property(x => x.LinkKind).HasMaxLength(20);
            e.Property(x => x.LinkLabel).HasMaxLength(300);
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
