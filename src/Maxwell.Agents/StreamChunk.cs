namespace Maxwell.Agents;

/// <summary>
/// One piece of a streamed reply. <see cref="IsReasoning"/> is true for
/// reasoning/"thinking" text (from streamed TextReasoningContent items) and
/// false for the model's actual answer text (TextContent), so callers can
/// render the two differently instead of mixing them together.
/// </summary>
public readonly record struct StreamChunk(string Text, bool IsReasoning);
