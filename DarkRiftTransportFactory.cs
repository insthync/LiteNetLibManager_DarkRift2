using LiteNetLibManager;

public class DarkRiftTransportFactory : BaseTransportFactory
{
    public override ITransport Build()
    {
        return new DarkRiftTransport();
    }
}
