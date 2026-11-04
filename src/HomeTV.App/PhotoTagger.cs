using HomeTV.Core.Photos;
using HomeTV.Core.Session;

namespace HomeTV.App;

/// <summary>
/// Tags background photos in the background: on start, after each upload (<see cref="Poke"/>), and every few minutes
/// for photos dropped into the folder by hand. One photo at a time, and the slideshow is told when tags change so the
/// People/Nature/Safe-for-work modes fill in as it goes. Hand-set tags are never overwritten.
/// </summary>
internal sealed class PhotoTagger(
    PhotoLibrary photos,
    PhotoTagStore tags,
    ClipPhotoClassifier classifier,
    SessionState session,
    ILogger<PhotoTagger> logger) : BackgroundService
{
    private static readonly TimeSpan Rescan = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryModel = TimeSpan.FromHours(1);
    /// <summary>Tell the launcher after this many new tags, so a big first batch shows up bit by bit.</summary>
    private const int PublishEvery = 10;

    private readonly SemaphoreSlim _poke = new(0, 1);

    /// <summary>How many photos are waiting to be looked at (for Settings).</summary>
    public int Pending { get; private set; }

    public string? Status => classifier.Status;

    /// <summary>Look at new photos now rather than at the next rescan.</summary>
    public void Poke()
    {
        try { _poke.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup finish (and the launcher draw) before loading a 350 MB model.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await classifier.InitializeAsync(stoppingToken);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Photo sorting model not available; trying again in {Delay}", RetryModel);
                await Task.Delay(RetryModel, stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TagNewAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Photo sorting pass failed");
            }
            await _poke.WaitAsync(Rescan, stoppingToken);
        }
    }

    private async Task TagNewAsync(CancellationToken ct)
    {
        var list = photos.List();
        var known = tags.All();
        var changed = tags.Prune(list.Select(p => p.Name));
        var todo = list
            .Where(p => p.Kind == PhotoKind.Image)
            .Where(p => known.GetValueOrDefault(p.Name) is not { } t || (!t.Manual && t.Version != p.Version))
            .ToList();
        Pending = todo.Count;
        var sincePublish = 0;
        foreach (var photo in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (photos.Resolve(photo.Name) is not { } path)
            {
                Pending--;
                continue;
            }
            try
            {
                var result = await classifier.ClassifyAsync(path, ct);
                // Someone may have tagged it by hand meanwhile.
                if (tags.Get(photo.Name) is not { Manual: true })
                    tags.Set(photo.Name, result with { Version = photo.Version });
                logger.LogInformation("Sorted {Name}: people={People} nature={Nature} sfw={Sfw}", photo.Name, result.People, result.Nature, result.SafeForWork);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unreadable: remember it as seen (all false) so it isn't retried every pass; it still shows in "All".
                logger.LogWarning(ex, "Couldn't sort {Name}", photo.Name);
                tags.Set(photo.Name, new PhotoTags(false, false, false, false, photo.Version));
            }
            Pending--;
            changed = true;
            if (++sincePublish >= PublishEvery)
            {
                session.Publish(new PhotosChangedEvent());
                sincePublish = 0;
                changed = false;
            }
        }
        Pending = 0;
        if (changed)
            session.Publish(new PhotosChangedEvent());
    }
}
