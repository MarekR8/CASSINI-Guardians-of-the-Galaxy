namespace Cassini.Demo;

internal readonly record struct RoadBounds(double West, double South, double East, double North)
{
    internal bool Intersects(double[][] line)
    {
        for (var i = 1; i < line.Length; i++)
            if (Intersects(line[i - 1], line[i]))
                return true;
        return false;
    }

    private bool Intersects(double[] start, double[] end)
    {
        var first = 0d;
        var last = 1d;
        return Clip(start[0], end[0], West, East) && Clip(start[1], end[1], South, North);

        bool Clip(double a, double b, double low, double high)
        {
            if (a == b)
                return a >= low && a <= high;
            var entry = (low - a) / (b - a);
            var exit = (high - a) / (b - a);
            first = Math.Max(first, Math.Min(entry, exit));
            last = Math.Min(last, Math.Max(entry, exit));
            return first <= last;
        }
    }
}
