namespace HomeTV.Core.Remote;

/// <summary>
/// Scales one connection's <see cref="MouseMoveCommand"/>s by a sensitivity multiplier. Keeps the rounding
/// remainder between messages so slow drags still move the pointer at low sensitivity.
/// </summary>
public sealed class PointerScaler
{
    private double _remainderX;
    private double _remainderY;

    public MouseMoveCommand Scale(MouseMoveCommand move, double speed)
    {
        var x = move.Dx * speed + _remainderX;
        var y = move.Dy * speed + _remainderY;
        var dx = (int)Math.Round(x);
        var dy = (int)Math.Round(y);
        _remainderX = x - dx;
        _remainderY = y - dy;
        return new MouseMoveCommand(dx, dy);
    }
}
