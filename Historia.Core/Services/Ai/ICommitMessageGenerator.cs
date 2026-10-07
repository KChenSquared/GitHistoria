namespace Historia.Services.Ai;

/// <summary>A generated message. <see cref="ReplacedModel"/> and <see cref="NewModel"/> are set
/// when the saved Gemini model was gone and another was chosen and saved; the old one is null when
/// none had been set.</summary>
public sealed record CommitMessageResult(string Message, string? ReplacedModel, string? NewModel);

/// <summary>
/// Writes a commit message for the staged changes with the active AI integration. One request:
/// the staged file list and diff. Runs only because the user asked; construction does nothing.
/// Failures are <see cref="AiException"/>; cancellation is <see cref="OperationCanceledException"/>.
/// </summary>
public interface ICommitMessageGenerator
{
    /// <param name="amend">Take the diff against <c>HEAD^</c>, so the amended commit's own changes are included.</param>
    Task<CommitMessageResult> GenerateAsync(RepositoryContext ctx, bool amend, CancellationToken ct);
}
