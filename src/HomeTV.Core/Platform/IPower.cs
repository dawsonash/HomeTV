namespace HomeTV.Core.Platform;

public interface IPower
{
    /// <summary>Puts the PC to sleep (S3 / standby). Never hibernates.</summary>
    void Sleep();
}
