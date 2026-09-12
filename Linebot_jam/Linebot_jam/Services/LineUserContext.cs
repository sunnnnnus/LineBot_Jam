using Linebot_jam.Data;
using Linebot_jam.Models;
using Linebot_jam.Models.Line;
using Microsoft.EntityFrameworkCore;

namespace Linebot_jam.Services;

/// <summary>One verified webhook event's user. Never initialize from AI arguments or message text.</summary>
public sealed class LineUserContext(AppDbContext db)
{
    private User? _user;

    public User User => _user ?? throw new InvalidOperationException("LINE user has not been resolved.");

    // All interactive task queries start here, including future update/delete features.
    public IQueryable<TaskItem> Tasks
    {
        get
        {
            var userId = User.Id;
            return db.Tasks.Where(task => task.UserId == userId);
        }
    }

    public async Task<User?> ResolveAsync(LineSource? source, CancellationToken cancellationToken = default)
    {
        var lineUserId = source?.UserId;
        if (string.IsNullOrWhiteSpace(lineUserId) || lineUserId.Length > 50 || lineUserId.Any(char.IsWhiteSpace))
            return null;

        if (_user is not null)
        {
            if (_user.LineUserId != lineUserId)
                throw new InvalidOperationException("A user context cannot be reused for another LINE user.");
            return _user;
        }

        // Preserve existing IDs and pending state. Concurrent first messages cannot create duplicates.
        // This participates in the worker's existing business/outbox transaction.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"USERS\" (\"LineUserId\") VALUES ({lineUserId}) ON CONFLICT (\"LineUserId\") DO NOTHING",
            cancellationToken);
        _user = await db.Users.SingleAsync(user => user.LineUserId == lineUserId, cancellationToken);
        return _user;
    }

    public void AddTask(TaskItem task)
    {
        task.User = User;
        task.UserId = User.Id;
        db.Tasks.Add(task);
    }
}
