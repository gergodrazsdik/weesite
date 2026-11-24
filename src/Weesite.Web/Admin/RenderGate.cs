using System.Collections.Concurrent;
using System.Security.Claims;

namespace Weesite.Web.Admin;

/// <summary>
/// One render on demand at a time per editor (T-0177): the live preview, MCP's <c>preview_item</c>
/// and the HTML exports. Renders are cached by text, so this only matters for new text, which near
/// the length limit can take seconds; an editor's second render while one runs is refused rather
/// than queued, and the preview catches up with the next pause in typing.
/// </summary>
internal sealed class RenderGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes the editor's turn, if no render of theirs is running.</summary>
    /// <param name="user">The signed-in editor.</param>
    /// <returns>The turn, to dispose when the render is done; null while another runs.</returns>
    public IDisposable? TryEnter(ClaimsPrincipal user)
    {
        var gate = _gates.GetOrAdd(EditorClaims.Email(user), _ => new SemaphoreSlim(1, 1));
        return gate.Wait(0) ? new Turn(gate) : null;
    }

    private sealed class Turn(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
