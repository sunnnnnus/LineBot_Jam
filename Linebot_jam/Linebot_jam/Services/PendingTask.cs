namespace Linebot_jam.Services;

/// 等待使用者確認的待辦提議,序列化成 JSON 存在 USERS.PendingTasksJson。
public record PendingTask(string Content, DateTime DueAt);
