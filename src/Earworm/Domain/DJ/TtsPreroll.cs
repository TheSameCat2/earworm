using System;
using System.Threading.Tasks;

namespace Earworm.Domain.DJ;

/// <summary>
/// Returned by <see cref="DJEngine.MaybePlayCommentaryAsync"/> to ask
/// PlayerEngine to play a TTS audio file ahead of the upcoming music track.
/// Includes a cleanup callback so the staged .mp3 can be deleted from disk
/// once Lavalink has finished streaming it.
/// </summary>
public sealed record TtsPreroll(string Url, Func<Task> OnConsumedAsync);
