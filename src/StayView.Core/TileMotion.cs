namespace StayView.Core;

public readonly record struct TileMotion(Native.RECT From, Native.RECT To, long Started)
{
    // Continue from the last displayed pixels, never the old destination. An unchanged
    // destination retains its clock so repeated pointer samples cannot stall the easing.
    public TileMotion Retarget(Native.RECT displayed, Native.RECT destination, long now)
        => To.Equals(destination) ? this : new(displayed, destination, now);
}
