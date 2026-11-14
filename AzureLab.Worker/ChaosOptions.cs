namespace AzureLab.Worker;
public class ChaosOptions
{
    public string? Mode { get; set; }
    public int SlowSeconds { get; set; } = 45;
}