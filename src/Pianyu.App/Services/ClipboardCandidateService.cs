using Pianyu.App.Data;
using Pianyu.Core;

namespace Pianyu.App.Services;

public sealed record ClipboardCandidate(Snippet Snippet, bool FromListener, DateTimeOffset CreatedAt);

public sealed class ClipboardCandidateService(SnippetRepository repository)
{
    public ClipboardCandidate Create(string text, bool fromListener)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("剪贴板中没有纯文本。", nameof(text));
        return new ClipboardCandidate(new Snippet
        {
            Title = SnippetRepository.FirstLine(text),
            Content = text.Trim()
        }, fromListener, DateTimeOffset.Now);
    }

    public Task<(Snippet? Snippet, bool IsDuplicate)> ConfirmAsync(Snippet snippet, CancellationToken cancellationToken = default) =>
        repository.SaveAsync(snippet, cancellationToken);
}
