using Maxwell.Agents.Tools.Search;

namespace Maxwell.Agents.Tools;

/// <summary>
/// Search hints for the task board tools, keyed by tool name: synonyms and literal phrases their
/// names and descriptions lack, so search_tools("delegate a job") finds create_task. Kept in its
/// own file (not inside AgentToolset) so the tests exercise exactly this data.
///
/// Rule of thumb for which kind of hint to use:
///  - Keywords for *words* that mean the tool ("delegate", "dispatch"). Stemmed, so inflections match.
///  - Phrases where the *exact wording* decides which tool is meant. Stemming erases tense and word
///    order, and "tasks I created" (list my tasks) and "create a task" (make one) are the same
///    words to a stemmer. A literal phrase only matches when it's written that way.
/// </summary>
public static class TaskBoardSearchHints
{
    public static IReadOnlyDictionary<string, ToolSearchHints> All { get; } =
        new Dictionary<string, ToolSearchHints>(StringComparer.OrdinalIgnoreCase)
        {
            ["create_task"] = new()
            {
                Keywords = ["delegate", "dispatch", "hand off", "assign work", "schedule", "enqueue", "todo", "subtask", "workflow", "new job"],
                Phrases = ["create a task", "create a new task", "add a task", "make a task"],
            },
            ["list_tasks"] = new()
            {
                Keywords = ["queue", "inbox", "backlog", "pending", "progress", "status", "check results"],
                Phrases =
                [
                    "tasks i created", "tasks i delegated", "tasks i assigned", "my tasks", "my queue",
                    "created by me", "assigned to me", "what did i create", "what did i delegate",
                ],
            },
            ["claim_task"] = new()
            {
                Keywords = ["start", "begin", "start working", "begin working", "accept", "pick up", "take on"],
            },
            ["complete_task"] = new()
            {
                Keywords = ["finish", "finished", "done", "submit", "report result", "close"],
                Phrases = ["mark it done", "mark as done", "i am done", "i m done", "i finished"],
            },
            ["fail_task"] = new()
            {
                Keywords = ["abort", "give up", "blocked", "unable", "cannot", "error"],
                Phrases = ["i cannot do", "i can t do", "unable to complete"],
            },
        };
}
