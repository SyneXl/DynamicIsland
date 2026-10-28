using System.Windows;

namespace DynamicIsland;

sealed class Parting
{
    const double Gap = 9, Softness = 3, Reach = 38, Height = 13;
    const double Bow = 3, BowSoftness = 4, BowReach = 22;
    const double Stiffness = 380, Damping = 25;

    readonly Point[] _middles;
    readonly Spring[] _x, _y;

    public Parting(Point[] middles)
    {
        _middles = middles;
        _x = [.. middles.Select(_ => new Spring(0, Stiffness, Damping))];
        _y = [.. middles.Select(_ => new Spring(0, Stiffness, Damping))];
    }

    public int Count => _middles.Length;

    public Vector this[int index] => new(_x[index].Value, _y[index].Value);

    public bool Advance(Point? pointer, double dt)
    {
        bool moving = false;
        for (int i = 0; i < _middles.Length; i++)
        {
            Vector push = pointer is { } at ? Push(_middles[i] - at) : default;
            _x[i].Target = push.X;
            _y[i].Target = push.Y;
            moving |= _x[i].Advance(dt) | _y[i].Advance(dt);
        }
        return moving;
    }

    static Vector Push(Vector away)
    {
        double nearness = Math.Exp(-Square(away.Y / Height));
        return new Vector(
            nearness * Gap * Math.Tanh(away.X / Softness) * Math.Exp(-Square(away.X / Reach)),
            nearness * Bow * Math.Tanh(away.Y / BowSoftness) * Math.Exp(-Square(away.X / BowReach)));
    }

    static double Square(double value) => value * value;
}
