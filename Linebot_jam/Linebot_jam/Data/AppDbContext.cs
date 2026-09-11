using System;
using System.Collections.Generic;
using Linebot_jam.Models;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Data;

public partial class AppDbContext : DbContext
{
    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<ReminderLog> ReminderLogs { get; set; }

    public virtual DbSet<TaskItem> Tasks { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public DbSet<WebhookJob> WebhookJobs { get; set; }

    // Fallback used only by design-time tooling (e.g. `dotnet ef migrations`) when no DI-provided
    // options are available. The IsConfigured check is essential: without it, this unconditionally
    // overrides the real connection string that Program.cs builds from Render's env vars, silently
    // forcing every environment back onto localhost.
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=LinebotJam;Username=postgres;Password=CHANGE_ME");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookJob>(entity =>
        {
            entity.ToTable("WEBHOOK_JOBS");
            entity.HasKey(e => e.EventId);
            entity.Property(e => e.Sequence).UseIdentityAlwaysColumn();
        });

        modelBuilder.Entity<ReminderLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("REMINDER_LOGS_pkey");

            entity.ToTable("REMINDER_LOGS");

            entity.HasIndex(e => new { e.TaskId, e.ReminderType }, "UQ_ReminderLogs_Task_Type").IsUnique();

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Channel).HasMaxLength(20);
            entity.Property(e => e.ReminderType).HasMaxLength(20);
            entity.Property(e => e.SentAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.Task).WithMany(p => p.ReminderLogs)
                .HasForeignKey(d => d.TaskId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ReminderLogs_Tasks");
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("TASKS_pkey");

            entity.ToTable("TASKS");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Content).HasMaxLength(200);
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.DueAt).HasColumnType("timestamp without time zone");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValueSql("'pending'::character varying");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone");

            entity.HasOne(d => d.User).WithMany(p => p.Tasks)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Tasks_Users");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("USERS_pkey");

            entity.ToTable("USERS");

            entity.HasIndex(e => e.LineUserId, "USERS_LineUserId_key").IsUnique();

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnType("timestamp without time zone");
            entity.Property(e => e.DisplayName).HasMaxLength(100);
            entity.Property(e => e.LineUserId).HasMaxLength(50);
            entity.Property(e => e.PendingRawInput).HasMaxLength(1000);
            entity.Property(e => e.PendingUpdatedAt).HasColumnType("timestamp without time zone");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
