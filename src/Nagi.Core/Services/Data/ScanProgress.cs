namespace Nagi.Core.Services.Data;

public class ScanProgress
{
    public string StatusText { get; set; } = string.Empty;
    public double Percentage { get; set; }
    public bool IsIndeterminate { get; set; }
}
