using System.Collections.Concurrent;
using System.Security.Claims;

namespace Weesite.Web.Admin;

/// <summary>
/// One render on demand at a time per editor (T-0177): the live preview, MCP's <c>preview_item</c>
/// and the HTML exports. Renders are cached by text, so this only matters for new text, which near
/// the length limit can take seconds. An editor's next render waits a little for its turn (two
/// tabs, or an export during a preview), then is refused; the preview catches up with the next
/// pause in typing.
/// </summary>
internal sealed class RenderGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long the live preview waits for its turn.</summary>
    public static readonly TimeSpan PreviewWait = TimeSpan.FromSeconds(3);

    /// <summary>How long an export or MCP's <c>preview_item</c> waits for its turn.</summary>
    public static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(15);

    /// <summary>Takes the editor's turn, waiting up to a while for a render of theirs to finish.</summary>
    /// <param name="user">The signed-in editor.</param>
    /// <param name="wait">How long to wait for it.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <returns>The turn, to dispose when the render is done; null when another was still running.</returns>
    public async Task<IDisposable?> EnterAsync(ClaimsPrincipal user, TimeSpan wait, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(EditorClaims.Email(user), _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(wait, cancellationToken) ? new Turn(gate) : null;
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
