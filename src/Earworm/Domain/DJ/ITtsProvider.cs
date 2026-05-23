using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Earworm.Domain.DJ;

public interface ITtsProvider
{
    /// <summary>
    /// Renders the specified text to an audio stream (typically MP3 or Opus).
    /// </summary>
    Task<Stream> RenderTtsAsync(string text, CancellationToken cancellationToken);
}
