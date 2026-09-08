using System.Text.Json;
using System.Text.Json.Serialization;

namespace G19Claude;

public enum TaskState { Pending, InProgress, Completed, Blocked }

public sealed record TaskItem(int Id, string Subject, string ActiveForm, TaskState State)
{
    /// <summary>The running form reads better while a task is active ("Schreibe ..." vs "... schreiben").</summary>
    public string Display => State == TaskState.InProgress && !string.IsNullOrWhiteSpace(ActiveForm)
        ? ActiveForm
        : Subject;
}

/// <summary>
/// Reads Claude Code's todo list for a session from ~/.claude/tasks/&lt;sessionId&gt;/.
///
/// One small JSON file per task, named by its id. Sessions that never used a todo list simply
/// have no directory, which is a normal state and not an error.
/// </summary>
public sealed class TaskStore
{
    private readonly string _root;

    private string _sessionId = "";
    private DateTime _lastWrite = DateTime.MinValue;
    private IReadOnlyList<TaskItem> _tasks = Array.Empty<TaskItem>();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public TaskStore()
    {
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "tasks");
    }

    public IReadOnlyList<TaskItem> Tasks => _tasks;

    /// <summary>
    /// Re-reads only when the directory's timestamp moved, so the common case costs one stat
    /// call rather than opening every task file each second.
    /// </summary>
    public void Refresh(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            _tasks = Array.Empty<TaskItem>();
            return;
        }

        var directory = Path.Combine(_root, sessionId);

        if (!Directory.Exists(directory))
        {
            _tasks = Array.Empty<TaskItem>();
            _sessionId = sessionId;
            _lastWrite = DateTime.MinValue;
            return;
        }

        var stamp = Directory.GetLastWriteTimeUtc(directory);
        if (sessionId == _sessionId && stamp == _lastWrite) return;

        _sessionId = sessionId;
        _lastWrite = stamp;

        var items = new List<TaskItem>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<TaskDto>(File.ReadAllText(file), Options);
                if (dto?.Subject is null) continue;

                items.Add(new TaskItem(
                    Id: int.TryParse(dto.Id, out var id) ? id : 0,
                    Subject: dto.Subject,
                    ActiveForm: dto.ActiveForm ?? "",
                    State: Parse(dto.Status, dto.BlockedBy)));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // A task file being rewritten mid-read is expected; the next refresh gets it.
            }
        }

        _tasks = items.OrderBy(t => t.Id).ToList();
    }

    private static TaskState Parse(string? status, List<string>? blockedBy)
    {
        if (blockedBy is { Count: > 0 } && status is not ("completed" or "in_progress"))
            return TaskState.Blocked;

        return status switch
        {
            "completed" => TaskState.Completed,
            "in_progress" => TaskState.InProgress,
            _ => TaskState.Pending,
        };
    }

    private sealed class TaskDto
    {
        public string? Id { get; set; }
        public string? Subject { get; set; }
        public string? ActiveForm { get; set; }
        public string? Status { get; set; }

        [JsonPropertyName("blockedBy")]
        public List<string>? BlockedBy { get; set; }
    }
}
