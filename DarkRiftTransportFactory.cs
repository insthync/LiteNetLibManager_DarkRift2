﻿using LiteNetLibManager;

public class DarkRiftTransportFactory : BaseTransportFactory
{
    public override bool CanUseWithWebGL { get { return false; } }

    public override ITransport Build()
    {
        return new DarkRiftTransport();
    }
}
