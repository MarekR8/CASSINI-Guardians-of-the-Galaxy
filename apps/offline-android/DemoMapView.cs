using Android.Content;
using Android.Graphics;
using Android.Views;
using Path = Android.Graphics.Path;

namespace Cassini.Offline;

internal sealed class DemoMapView(Context context) : View(context)
{
    private readonly Paint paint = new(PaintFlags.AntiAlias);
    private CopernicusLayer? layer;
    private string start = OfflineScenario.InitialNode;
    private IReadOnlySet<string> closed = new HashSet<string>();
    private WalkingRoute? route;

    internal void DisplayDemo(string origin, IReadOnlySet<string> closures, WalkingRoute? instruction)
    {
        layer = null;
        start = origin;
        closed = closures;
        route = instruction;
        ContentDescription = "Schemat demo z obszarem powodzi, drogami i zamknięciami.";
        Invalidate();
    }

    internal void DisplayLayer(CopernicusLayer evidence)
    {
        layer = evidence;
        ContentDescription = "Mapa obszarów wczytanych z lokalnego pliku Copernicus EMS.";
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        canvas.DrawColor(Color.Rgb(236, 243, 248));
        var scale = Resources?.DisplayMetrics?.Density ?? 1;
        if (layer is { } evidence)
        {
            var project = Project(evidence.Polygons.SelectMany(polygon => polygon).SelectMany(ring => ring), scale);
            foreach (var polygon in evidence.Polygons)
                DrawPolygon(canvas, polygon, project);
            return;
        }

        var location = Project(OfflineScenario.Nodes.Values.Concat(OfflineScenario.Flood), scale);
        DrawPolygon(canvas, [OfflineScenario.Flood], location);
        foreach (var segment in OfflineScenario.Segments)
        {
            var from = location(OfflineScenario.Nodes[segment.From]);
            var to = location(OfflineScenario.Nodes[segment.To]);
            var blocked = closed.Contains(segment.Id);
            var active = route?.Segments.Contains(segment.Id) ?? false;
            paint.Color = blocked ? Color.Rgb(164, 45, 51) : active ? Color.Rgb(16, 116, 83) : Color.Rgb(95, 117, 135);
            paint.StrokeWidth = (blocked || active ? 5 : 2) * scale;
            paint.StrokeCap = Paint.Cap.Round;
            canvas.DrawLine(from.X, from.Y, to.X, to.Y, paint);
            if (blocked)
            {
                var x = (from.X + to.X) / 2;
                var y = (from.Y + to.Y) / 2;
                var radius = 6 * scale;
                canvas.DrawLine(x - radius, y - radius, x + radius, y + radius, paint);
                canvas.DrawLine(x + radius, y - radius, x - radius, y + radius, paint);
            }
        }

        DrawPoint(canvas, location(OfflineScenario.Nodes[start]), "Start", scale);
        DrawPoint(canvas, location(OfflineScenario.Nodes[OfflineScenario.Exits[0]]), "Wschód", scale);
        DrawPoint(canvas, location(OfflineScenario.Nodes[OfflineScenario.Exits[1]]), "Północ", scale);
    }

    private Func<MapPoint, ScreenPoint> Project(IEnumerable<MapPoint> points, float density)
    {
        var coordinates = points.ToArray();
        var minLon = coordinates.Min(point => point.Longitude);
        var maxLon = coordinates.Max(point => point.Longitude);
        var minLat = coordinates.Min(point => point.Latitude);
        var maxLat = coordinates.Max(point => point.Latitude);
        var horizontalAdjustment = Math.Max(0.01, Math.Cos((maxLat + minLat) * Math.PI / 360));
        var spanX = Math.Max(0.00001, (maxLon - minLon) * horizontalAdjustment);
        var spanY = Math.Max(0.00001, maxLat - minLat);
        var padding = 32 * density;
        var ratio = Math.Max(0, Math.Min((Width - 2 * padding) / spanX, (Height - 2 * padding) / spanY));
        var centerLat = (minLat + maxLat) / 2;
        var centerLon = (minLon + maxLon) / 2;
        return point => new ScreenPoint(
            (float)(Width / 2d + (point.Longitude - centerLon) * horizontalAdjustment * ratio),
            (float)(Height / 2d - (point.Latitude - centerLat) * ratio));
    }

    private void DrawPolygon(Canvas canvas, IEnumerable<MapPoint[]> polygon, Func<MapPoint, ScreenPoint> project)
    {
        using var path = new Path();
        path.SetFillType(Path.FillType.EvenOdd!);
        foreach (var ring in polygon)
        {
            var first = project(ring[0]);
            path.MoveTo(first.X, first.Y);
            for (var index = 1; index < ring.Length; index++)
            {
                var point = project(ring[index]);
                path.LineTo(point.X, point.Y);
            }
            path.Close();
        }

        paint.Color = Color.Argb(128, 99, 173, 216);
        canvas.DrawPath(path, paint);
    }

    private void DrawPoint(Canvas canvas, ScreenPoint point, string name, float scale)
    {
        paint.Color = Color.Rgb(27, 52, 75);
        canvas.DrawCircle(point.X, point.Y, 5 * scale, paint);
        paint.TextSize = 12 * scale;
        paint.TextAlign = Paint.Align.Center;
        canvas.DrawText(name, point.X, point.Y - 8 * scale, paint);
    }

    private readonly record struct ScreenPoint(float X, float Y);
}
