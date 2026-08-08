using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.Shared.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Convolution;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Configuration = SixLabors.ImageSharp.Configuration;
using Vector4 = System.Numerics.Vector4;

namespace Radar;

public partial class Radar
{
    // Just moved this. Wasn't sure if the temp.png thing was actually needed or was leftover debugging.
    private void GenerateMapTexture()
    {
        var image = BuildMapImage();
        if (image == null)
            return;
        using (image)
        {
            //unfortunately the library doesn't respect our allocation settings inside BuildMapImage
            using var imageCopy = image.Clone();
            Graphics.AddOrUpdateImage(TextureName, imageCopy);
        }
    }

    // Just moved this into its own method to share the logic between PNG and SVG generation.
    // Was originally returning the existing png but switched to svg for scaleability
    private Image<Rgba32> BuildMapImage()
    {
        if (_areaDimensions == null || _heightData == null || _processedTerrainData == null ||
            _areaDimensions.Value.X <= 0 || _areaDimensions.Value.Y <= 0)
            return null;

        var gridHeightData = _heightData;
        var maxX = _areaDimensions.Value.X;
        var maxY = _areaDimensions.Value.Y;
        var configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        var image = new Image<Rgba32>(configuration, maxX, maxY);
        if (Settings.Debug.DrawHeightMap)
        {
            var minHeight = gridHeightData.Min(x => x.Min());
            var maxHeight = gridHeightData.Max(x => x.Max());
            var heightRange = MathF.Abs(maxHeight - minHeight) > float.Epsilon ? maxHeight - minHeight : 1f;
            image.Mutate(configuration, c => c.ProcessPixelRowsAsVector4((row, i) =>
            {
                for (var x = 0; x < row.Length - 1; x += 2)
                {
                    var cellData = gridHeightData[i.Y][x];
                    for (var x_s = 0; x_s < 2; ++x_s)
                    {
                        row[x + x_s] = new Vector4(0, (cellData - minHeight) / heightRange, 0, 1);
                    }
                }
            }));
        }
        else
        {
            if (Settings.Debug.AlternativeEdgeMethod)
            {
                static float Clamp(float value, float min, float max) => MathF.Min(MathF.Max(value, min), max);

                static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a + t * (b - a);

                using var binaryMap = new Image<L8>(configuration, maxX, maxY);

                if (!Settings.Debug.DisableHeightAdjust)
                    Parallel.For(
                        0, maxY, y =>
                        {
                            for (var x = 0; x < maxX; x++)
                            {
                                if (_processedTerrainData[y][x] != 1)
                                    continue;

                                var cellData = gridHeightData[y][x];
                                var heightOffset = (int)(cellData / GridToWorldMultiplier / 2);
                                var adjustedX = x - heightOffset;
                                var adjustedY = y - heightOffset;

                                if (adjustedX >= 0 && adjustedX < maxX && adjustedY >= 0 && adjustedY < maxY)
                                    binaryMap[adjustedX, adjustedY] = new L8(255);
                            }
                        });

                else
                    Parallel.For(
                        0, maxY, y =>
                        {
                            for (var x = 0; x < maxX; x++)
                                if (_processedTerrainData[y][x] != 1)
                                    binaryMap[x, y] = new L8(255);
                        });

                var blurSigma = Settings.Debug.AlternativeEdgeSettings.OutlineBlurSigma.Value;
                binaryMap.Mutate(ctx => ctx.GaussianBlur(blurSigma));

                var eps = Settings.Debug.AlternativeEdgeSettings.OutlineTransitionThreshold.Value;
                var aaWidth = MathF.Max(Settings.Debug.AlternativeEdgeSettings.OutlineFeatherWidth.Value, 0.0001f);

                Parallel.For(
                    0, maxY, y =>
                    {
                        for (var x = 0; x < maxX; x++)
                        {
                            var t = binaryMap[x, y].PackedValue / 255f;

                            var walkableToEdge = Clamp((t - (eps - aaWidth)) / aaWidth, 0f, 1f);
                            var edgeToUnwalkable = Clamp((t - (1f - eps)) / aaWidth, 0f, 1f);

                            var walkableColor = new Vector4(1f, 1f, 1f, 0.00f);
                            var outlineTarget = Settings.TerrainColor.Value.ToImguiVec4();

                            var color = Lerp(walkableColor, outlineTarget, walkableToEdge);
                            color = Lerp(color, color with { W = 0f }, edgeToUnwalkable);

                            color = new Vector4(Clamp(color.X, 0f, 1f), Clamp(color.Y, 0f, 1f), Clamp(color.Z, 0f, 1f), Clamp(color.W, 0f, 1f));

                            image[x, y] = new Rgba32(color.X, color.Y, color.Z, color.W);
                        }
                    });
            }
            else
            {
                var unwalkableMask = Vector4.UnitX + Vector4.UnitW;
                var walkableMask = Vector4.UnitY + Vector4.UnitW;
                if (Settings.Debug.DisableHeightAdjust)
                {
                    Parallel.For(0, maxY, y =>
                    {
                        for (var x = 0; x < maxX; x++)
                        {
                            var terrainType = _processedTerrainData[y][x];
                            image[x, y] = new Rgba32(terrainType is 0 ? unwalkableMask : walkableMask);
                        }
                    });
                }
                else
                {
                    Parallel.For(0, maxY, y =>
                    {
                        for (var x = 0; x < maxX; x++)
                        {
                            var cellData = gridHeightData[y][x / 2 * 2];

                            //basically, offset x and y by half the offset z would cause when rendering in 3d
                            var heightOffset = (int)(cellData / GridToWorldMultiplier / 2);
                            var offsetX = x - heightOffset;
                            var offsetY = y - heightOffset;
                            var terrainType = _processedTerrainData[y][x];
                            if (offsetX >= 0 && offsetX < maxX && offsetY >= 0 && offsetY < maxY)
                            {
                                image[offsetX, offsetY] = new Rgba32(terrainType is 0 ? unwalkableMask : walkableMask);
                            }
                        }
                    });
                }

                if (!Settings.Debug.StandardEdgeSettings.SkipNeighborFill)
                {
                    Parallel.For(0, maxY, y =>
                    {
                        for (var x = 0; x < maxX; x++)
                        {
                            //this fills in the blanks that are left over from the height projection
                            if (image[x, y].ToVector4() == Vector4.Zero)
                            {
                                var countWalkable = 0;
                                var countUnwalkable = 0;
                                for (var xO = -1; xO < 2; xO++)
                                {
                                    for (var yO = -1; yO < 2; yO++)
                                    {
                                        var xx = x + xO;
                                        var yy = y + yO;
                                        if (xx >= 0 && xx < maxX && yy >= 0 && yy < maxY)
                                        {
                                            var nPixel = image[x + xO, y + yO].ToVector4();
                                            if (nPixel == walkableMask)
                                                countWalkable++;
                                            else if (nPixel == unwalkableMask)
                                                countUnwalkable++;
                                        }
                                    }
                                }

                                image[x, y] = new Rgba32(countWalkable > countUnwalkable ? walkableMask : unwalkableMask);
                            }
                        }
                    });
                }

                if (!Settings.Debug.StandardEdgeSettings.SkipEdgeDetector)
                {
                    var edgeDetector = new EdgeDetectorProcessor(EdgeDetectorKernel.Laplacian5x5, false)
                       .CreatePixelSpecificProcessor(configuration, image, image.Bounds);
                    edgeDetector.Execute();
                }

                if (!Settings.Debug.StandardEdgeSettings.SkipRecoloring)
                {
                    image.Mutate(configuration, c => c.ProcessPixelRowsAsVector4((row, p) =>
                    {
                        for (var x = 0; x < row.Length - 0; x++)
                        {
                            row[x] = row[x] switch
                            {
                                { X: 1 } => Settings.TerrainColor.Value.ToImguiVec4(),
                                { X: 0 } => Vector4.Zero,
                                var s => s
                            };
                        }
                    }));
                }
            }
        }

        if (Math.Max(image.Height, image.Width) > Settings.MaximumMapTextureDimension)
        {
            var (newWidth, newHeight) = (image.Width, image.Height);
            if (image.Height > image.Width)
            {
                newWidth = newWidth * Settings.MaximumMapTextureDimension / newHeight;
                newHeight = Settings.MaximumMapTextureDimension;
            }
            else
            {
                newHeight = newHeight * Settings.MaximumMapTextureDimension / newWidth;
                newWidth = Settings.MaximumMapTextureDimension;
            }

            var targetSize = new Size(newWidth, newHeight);
            var resizer = new ResizeProcessor(new ResizeOptions { Size = targetSize }, image.Size)
                .CreatePixelSpecificCloningProcessor(configuration, image, image.Bounds);
            resizer.Execute();
        }

        return image;
    }

    // Plugin-bridge entry point: build the current zone's map and return it as PNG bytes.
    // includeRoutes bakes the route paths + cluster target endpoints on top via OverlayRoutes.
    // This is used by the ExileStats plugin.
    private byte[] GetMapImageBytes(bool includeRoutes)
    {
        var image = BuildMapImage();
        if (image == null)
            return null;
        using (image)
        {
            try
            {
                if (includeRoutes)
                    OverlayRoutes(image);

                using var ms = new MemoryStream();
                image.SaveAsPng(ms);
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                DebugWindow.LogError(ex.ToString());
                return null;
            }
        }
    }

    // Bake the drawn route paths (and cluster target endpoints) onto the terrain image.
    private void OverlayRoutes(Image<Rgba32> image)
    {
        foreach (var route in _routes.Values)
        {
            if (route?.Path == null)
                continue;
            var c = route.MapColor();
            var px = new Rgba32(c.R, c.G, c.B, (byte)255);
            foreach (var p in route.Path)
                PlotDot(image, p.X, p.Y, px, 1);
        }

        var endpointColor = new Rgba32((byte)255, (byte)255, (byte)255, (byte)255);
        foreach (var target in _clusteredTargetLocations.Values)
        {
            if (target?.Locations == null)
                continue;
            foreach (var loc in target.Locations)
                PlotDot(image, (int)loc.X, (int)loc.Y, endpointColor, 2);
        }
    }

    private static void PlotDot(Image<Rgba32> image, int cx, int cy, Rgba32 color, int radius)
    {
        var xStart = Math.Max(0, cx - radius);
        var yStart = Math.Max(0, cy - radius);
        var xEnd = Math.Min(image.Width - 1, cx + radius);
        var yEnd = Math.Min(image.Height - 1, cy + radius);
        for (var y = yStart; y <= yEnd; y++)
            for (var x = xStart; x <= xEnd; x++)
                image[x, y] = color;
    }

    // Plugin-bridge entry point: build the current zone's map as an SVG string (infinitely scalable).
    private string GetMapSvgString(bool includeRoutes)
    {
        if (_areaDimensions == null || _processedTerrainData == null)
            return null;

        try
        {
            return BuildMapSvg(includeRoutes);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError(ex.ToString());
            return null;
        }
    }

    private string BuildMapSvg(bool includeRoutes)
    {
        var maxX = _areaDimensions.Value.X;
        var maxY = _areaDimensions.Value.Y;
        var terrain = _processedTerrainData;

        bool Walkable(int x, int y) =>
            x >= 0 && y >= 0 && x < maxX && y < maxY && terrain[y][x] != 0;

        var stride = (long)(maxX + 1);
        long Key(int px, int py) => py * stride + px;

        var edges = new HashSet<(int fx, int fy, int tx, int ty)>();
        void AddEdge(int fx, int fy, int tx, int ty)
        {
            if (!edges.Remove((tx, ty, fx, fy))) // annihilate the reverse if present
                edges.Add((fx, fy, tx, ty));
        }

        for (var y = 0; y < maxY; y++)
        {
            for (var x = 0; x < maxX; x++)
            {
                if (!Walkable(x, y))
                    continue;
                // corners: TL(x,y) TR(x+1,y) BR(x+1,y+1) BL(x,y+1), clockwise in SVG (y-down) coords
                AddEdge(x, y, x + 1, y);         // top
                AddEdge(x + 1, y, x + 1, y + 1); // right
                AddEdge(x + 1, y + 1, x, y + 1); // bottom
                AddEdge(x, y + 1, x, y);         // left
            }
        }

        var outgoing = new Dictionary<long, List<(int tx, int ty)>>(edges.Count);
        foreach (var (fx, fy, tx, ty) in edges)
        {
            var k = Key(fx, fy);
            if (!outgoing.TryGetValue(k, out var list))
                outgoing[k] = list = new List<(int, int)>(1);
            list.Add((tx, ty));
        }

        var path = new StringBuilder();
        foreach (var start in outgoing.Keys.ToList())
        {
            while (outgoing.TryGetValue(start, out var startList) && startList.Count > 0)
            {
                var loop = new List<(int x, int y)>();
                var cx = (int)(start % stride);
                var cy = (int)(start / stride);
                while (true)
                {
                    var ck = Key(cx, cy);
                    if (!outgoing.TryGetValue(ck, out var nexts) || nexts.Count == 0)
                        break; // loop closed (back at a fully-consumed start)
                    var n = nexts[^1];
                    nexts.RemoveAt(nexts.Count - 1);
                    loop.Add((cx, cy));
                    cx = n.tx;
                    cy = n.ty;
                    if (cx == (int)(start % stride) && cy == (int)(start / stride))
                        break;
                }
                AppendLoop(path, loop);
            }
        }

        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(maxX)
          .Append("\" height=\"").Append(maxY)
          .Append("\" viewBox=\"0 0 ").Append(maxX).Append(' ').Append(maxY).Append("\">\n");

        if (path.Length > 0)
            sb.Append("<path fill-rule=\"evenodd\" fill=\"").Append(ToHex(Settings.TerrainColor.Value))
              .Append("\" fill-opacity=\"").Append(F(Settings.TerrainColor.Value.A / 255f))
              .Append("\" d=\"").Append(path).Append("\"/>\n");

        if (includeRoutes)
            AppendRoutesSvg(sb);

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void AppendLoop(StringBuilder path, List<(int x, int y)> loop)
    {
        if (loop.Count < 3)
            return;

        var pts = new List<(int x, int y)>(loop.Count);
        for (var i = 0; i < loop.Count; i++)
        {
            var prev = loop[(i - 1 + loop.Count) % loop.Count];
            var cur = loop[i];
            var next = loop[(i + 1) % loop.Count];
            var d1x = cur.x - prev.x;
            var d1y = cur.y - prev.y;
            var d2x = next.x - cur.x;
            var d2y = next.y - cur.y;
            if (d1x * d2y - d1y * d2x != 0 || d1x * d2x + d1y * d2y < 0) // keep corners + reversals
                pts.Add(cur);
        }

        if (pts.Count < 3)
            return;

        path.Append('M').Append(pts[0].x).Append(' ').Append(pts[0].y);
        for (var i = 1; i < pts.Count; i++)
            path.Append('L').Append(pts[i].x).Append(' ').Append(pts[i].y);
        path.Append('Z');
    }

    private void AppendRoutesSvg(StringBuilder sb)
    {
        foreach (var route in _routes.Values)
        {
            if (route?.Path == null || route.Path.Count == 0)
                continue;
            var c = route.MapColor();
            sb.Append("<polyline fill=\"none\" stroke=\"").Append(ToHex(c))
              .Append("\" stroke-width=\"1\" stroke-linejoin=\"round\" points=\"");
            foreach (var p in route.Path)
                sb.Append(p.X).Append(',').Append(p.Y).Append(' ');
            sb.Append("\"/>\n");
        }

        foreach (var target in _clusteredTargetLocations.Values)
        {
            if (target?.Locations == null)
                continue;
            foreach (var loc in target.Locations)
                sb.Append("<circle cx=\"").Append(F(loc.X)).Append("\" cy=\"").Append(F(loc.Y))
                  .Append("\" r=\"2\" fill=\"#ffffff\"/>\n");
        }
    }

    private static string ToHex(System.Drawing.Color c) =>
        $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
