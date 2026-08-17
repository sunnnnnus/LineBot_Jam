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

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
#warning To protect potentially sensitive information in your connection string, you should move it out of source code. You can avoid scaffolding the connection string by using the Name= syntax to read it from configuration - see https://go.microsoft.com/fwlink/?linkid=2131148. For more guidance on storing connection strings, see https://go.microsoft.com/fwlink/?LinkId=723263.
        => optionsBuilder.UseSqlServer("Server=localhost\\SQLEXPRESS;Database=LinebotJam;Trusted_Connection=True;TrustServerCertificate=True;");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReminderLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__REMINDER__3214EC0790711C69");

            entity.ToTable("REMINDER_LOGS");

            entity.HasIndex(e => new { e.TaskId, e.ReminderType }, "UQ_ReminderLogs_Task_Type").IsUnique();

            entity.Property(e => e.Channel).HasMaxLength(20);
            entity.Property(e => e.ReminderType).HasMaxLength(20);
            entity.Property(e => e.SentAt).HasDefaultValueSql("(getdate())");

            entity.HasOne(d => d.Task).WithMany(p => p.ReminderLogs)
                .HasForeignKey(d => d.TaskId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ReminderLogs_Tasks");
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__TASKS__3214EC07C60489F9");

            entity.ToTable("TASKS");

            entity.Property(e => e.Content).HasMaxLength(200);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.Status)
                .HasMaxLength(20)
                .HasDefaultValue("pending");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("(getdate())");

            entity.HasOne(d => d.User).WithMany(p => p.Tasks)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Tasks_Users");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__USERS__3214EC0744F88F2F");

            entity.ToTable("USERS");

            entity.HasIndex(e => e.LineUserId, "UQ__USERS__1035A751EE398B96").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getdate())");
            entity.Property(e => e.DisplayName).HasMaxLength(100);
            entity.Property(e => e.LineUserId).HasMaxLength(50);
            entity.Property(e => e.PendingContent).HasMaxLength(200);
            entity.Property(e => e.PendingRawInput).HasMaxLength(1000);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
