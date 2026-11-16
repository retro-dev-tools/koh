namespace ClassBase;

public enum Marker : ushort
{
    Empty,
}

public class ExternalRoot
{
    public byte Hidden;
    public Marker State;
    public int Counter;
}
