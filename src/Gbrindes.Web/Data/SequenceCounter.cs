namespace Gbrindes.Web.Data;

public sealed class SequenceCounter
{
    public string Name { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public int LastValue { get; set; }
}
