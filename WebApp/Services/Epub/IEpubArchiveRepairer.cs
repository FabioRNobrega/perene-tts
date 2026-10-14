namespace PereneTts.Services.Epub;

public interface IEpubArchiveRepairer
{
    /// <summary>Returns the source unchanged when its mimetype marker conforms, otherwise a safely rebuilt copy.</summary>
    Task<PreparedEpub> PrepareAsync(Stream source, CancellationToken cancellation);
}
