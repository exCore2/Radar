using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Helpers;
using GameOffsets2;
using GameOffsets2.Native;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Vector2 = System.Numerics.Vector2;

namespace Radar;

public partial class Radar
{
    private Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task> _addRouteAction;
    private Func<Color> _getColor;
    
    private void LoadTargets()
    {
        var fileText = File.ReadAllText(Path.Combine(DirectoryFullName, "targets.json"));
        _targetDescriptions = JsonConvert.DeserializeObject<ConcurrentDictionary<string, List<TargetDescription>>>(fileText);
    }

    private void RestartPathFinding()
    {
        StopPathFinding();
        StartPathFinding();
    }

    private void StartPathFinding()
    {
        if (Settings.PathfindingSettings.ShowPathsToTargetsOnMap)
        {
            var e = RainbowColors.GetEnumerator();
            _getColor = () =>
            {
                if (!e.MoveNext())
                {
                    e = RainbowColors.GetEnumerator();
                    if (!e.MoveNext())
                    {
                        return Color.Green;
                    }
                }

                return e.Current;
            };
            var pf = new PathFinder(_processedTerrainData, new[] { 1, 2, 3, 4, 5 });
            _addRouteAction = (point, callback, cancellationToken) => Task.Run(() => FindPath(pf, point, callback, cancellationToken), cancellationToken);
            foreach (var (location, target) in _clusteredTargetLocations
                         .SelectMany(x => x.Value.Locations.Select(loc=>(loc, x.Value.Target)))
                         .DistinctBy(x => x.loc))
            {
                AddRoute(location, target, null);
            }
        }
    }

    private void AddRoute(Vector2 target, TargetDescription targetDescription, Entity entity)
    {
        var color = _getColor();

        Color GetWorldColor() => Settings.PathfindingSettings.WorldPathSettings.UseRainbowColorsForPaths
            ? color
            : Settings.PathfindingSettings.WorldPathSettings.DefaultPathColor;

        Color GetMapColor() => Settings.PathfindingSettings.UseRainbowColorsForMapPaths
            ? color
            : Settings.PathfindingSettings.DefaultMapPathColor;

        var routes = _routes;

        var cancellationToken = _findPathsCts.Token;
        if (targetDescription.TargetType == TargetType.Entity && entity != null)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = cts.Token;

            async Task CheckEntity()
            {
                while (entity.IsValid && entity.GetComponent<Chest>()?.IsOpened != true)
                {
                    await Task.Delay(100, cancellationToken);
                }

                await cts.CancelAsync();
                routes.Remove(target, out _);
            }

            _ = CheckEntity();
        }

        AddRoute(target, targetDescription, path =>
        {
            if (path != null)
            {
                Func<Color> customColorFunc = null;
                if (targetDescription.Color != null)
                {
                    var color = ConvertHelper.FromAbgr(uint.Parse(targetDescription.Color, NumberStyles.HexNumber));
                    customColorFunc = () => color;
                }

                var rd = new RouteDescription
                {
                    Path = path, 
                    MapColor = customColorFunc ?? GetMapColor,
                    WorldColor = customColorFunc ?? GetWorldColor
                };
                routes.AddOrUpdate(target, rd, (_, _) => rd);
            }
        }, cancellationToken);
    }

    private Task AddRoute(Vector2 target, TargetDescription targetDescription, Action<List<Vector2i>> callback, CancellationToken cancellationToken)
    {
        if (_addRouteAction == null)
        {
            return Task.FromException(new Exception("Pathfinding wasn't started yet"));
        }

        return _addRouteAction(target, callback, cancellationToken);
    }

    private void StopPathFinding()
    {
        _findPathsCts.Cancel();
        _findPathsCts = new CancellationTokenSource();
        _routes = new ConcurrentDictionary<Vector2, RouteDescription>();
        _addRouteAction = null;
    }

    private async Task WaitUntilPluginEnabled(CancellationToken cancellationToken)
    {
        while (!Settings.Enable)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task FindPath(PathFinder pf, Vector2 point, Action<List<Vector2i>> callback, CancellationToken cancellationToken)
    {
        var playerPosition = GetPlayerPosition();
        foreach (var elem in pf.RunFirstScan(new Vector2i((int)playerPosition.X, (int)playerPosition.Y), new Vector2i((int)point.X, (int)point.Y)))
        {
            await WaitUntilPluginEnabled(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (elem.Any())
            {
                callback(elem);
            }
        }

        while (true)
        {
            await WaitUntilPluginEnabled(cancellationToken);
            var newPosition = GetPlayerPosition();
            if (playerPosition == newPosition)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            playerPosition = newPosition;
            var path = pf.FindPath(new Vector2i((int)playerPosition.X, (int)playerPosition.Y), new Vector2i((int)point.X, (int)point.Y));
            callback(path);
        }
    }
	private List<Vector2i> FilterByClusterSize(IReadOnlyCollection<Vector2i> positions, int clusterSize)
	{
		if (clusterSize <= 1) return positions.ToList();
		
		var validClusters = new HashSet<Vector2i>();
		var positionSet = new HashSet<Vector2i>(positions);
		
		foreach (var pos in positions)
		{
			if (validClusters.Contains(pos)) continue;
			
			// Flood fill to find connected component
			var cluster = new HashSet<Vector2i>();
			var queue = new Queue<Vector2i>();
			queue.Enqueue(pos);
			cluster.Add(pos);
			
			while (queue.Count > 0)
			{
				var current = queue.Dequeue();
				
				// Check neighbors at TILE distance (23 grid units)
				var neighbors = new[]
				{
					new Vector2i(current.X + TileToGridConversion, current.Y),
					new Vector2i(current.X - TileToGridConversion, current.Y),
					new Vector2i(current.X, current.Y + TileToGridConversion),
					new Vector2i(current.X, current.Y - TileToGridConversion),
				};
				
				foreach (var neighbor in neighbors)
				{
					if (positionSet.Contains(neighbor) && !cluster.Contains(neighbor))
					{
						cluster.Add(neighbor);
						queue.Enqueue(neighbor);
					}
				}
			}
			
			// If cluster is large enough, add all positions in it
			if (cluster.Count >= clusterSize)
			{
				foreach (var p in cluster)
					validClusters.Add(p);
			}
		}
		
		return validClusters.ToList();
	}

    private ConcurrentDictionary<string, List<Vector2i>> GetTargets()
    {
        return new ConcurrentDictionary<string, List<Vector2i>>(GetTileTargets()
            .ToLookup(x => x.Key, x => x.Value)
            .ToDictionary(x => x.Key, x => x.SelectMany(v => v).ToList()));
    }

    //private Dictionary<string, List<Vector2i>> GetEntityTargets()
    //{
    //    return GameController.Entities.Where(x => x.HasComponent<Positioned>()).Where(x => _currentZoneTargetEntityPaths.ContainsKey(x.Path))
    //        .ToLookup(x => x.Path, x => x.GetComponent<Positioned>().GridPosNum.Truncate())
    //        .ToDictionary(x => x.Key, x => x.ToList());
    //}

    private Dictionary<string, List<Vector2i>> GetTileTargets()
    {
        var tileData = GameController.Memory.ReadStdVector<TileStructure>(_terrainMetadata.TgtArray);
        var ret = new ConcurrentDictionary<string, ConcurrentQueue<Vector2i>>();
        Parallel.For(0, tileData.Length, tileNumber =>
        {
            var tgtTileStruct = GameController.Memory.Read<TgtTileStruct>(tileData[tileNumber].TgtFilePtr);
            var key2 = GameController.Memory.Read<TgtDetailStruct>(tgtTileStruct.TgtDetailPtr)
                .name.ToString(GameController.Memory);
            var coordinate = new Vector2i(
                tileNumber % _terrainMetadata.NumCols * TileToGridConversion,
                tileNumber / _terrainMetadata.NumCols * TileToGridConversion);

            if (Settings.PathfindingSettings.IncludeTilePathsAsTargets)
            {
                var key1 = tgtTileStruct.TgtPath.ToString(GameController.Memory);
                if (!string.IsNullOrEmpty(key1))
                {
                    ret.GetOrAdd(key1, _ => new ConcurrentQueue<Vector2i>()).Enqueue(coordinate);
                }
            }

            if (!string.IsNullOrEmpty(key2))
            {
                ret.GetOrAdd(key2, _ => new ConcurrentQueue<Vector2i>()).Enqueue(coordinate);
            }
        });
        return ret.ToDictionary(k => k.Key, k => k.Value.ToList());
    }

    private bool IsDescriptionInArea(string descriptionAreaPattern)
    {
        return GameController.Area.CurrentArea.Area.RawName.Like(descriptionAreaPattern);
    }

    private IEnumerable<TargetDescription> GetTargetDescriptionsInArea()
    {
        return _targetDescriptions.Where(x => IsDescriptionInArea(x.Key)).SelectMany(x => x.Value);
    }

    private ConcurrentDictionary<string, TargetLocations> ClusterTargets()
    {
        var tileMap = new ConcurrentDictionary<string, TargetLocations>();
        Parallel.ForEach(_targetDescriptionsInArea.Values, new ParallelOptions { MaxDegreeOfParallelism = 1 }, target =>
        {
            var locations = ClusterTarget(target);
            if (locations != null)
            {
                tileMap[target.Name] = locations;
            }
        });
        return tileMap;
    }

    private IReadOnlyCollection<Vector2i> GetLocationsFromTilePattern(string tilePattern)
    {
        var regex = tilePattern.ToLikeRegex();
        return _allTargetLocations.Where(x => regex.IsMatch(x.Key)).SelectMany(x => x.Value).ToList();
    }

    private TargetLocations ClusterTarget(TargetDescription target)
    {
        // NEW: Use pattern matching if pattern is defined
        if (target.ClusterPattern != null && target.ClusterPattern.Length > 0)
        {
            var locations = FindPatternMatches(target);
            if (locations == null || locations.Count == 0)
                return null;

            return new TargetLocations
            {
                Locations = locations.Select(v => (Vector2)v).ToArray(),
                Target = target,
            };
        }

        // OLD: Fallback to original clustering for backward compatibility
        var expectedCount = target.ExpectedCount;
        var targetName = target.Name;
        var clusterSize = target.ClusterSize;
        var locations2 = ClusterTarget(targetName, expectedCount, clusterSize);
        if (locations2 == null) return null;

        return new TargetLocations
        {
            Locations = locations2,
            Target = target,
        };
    }

    private Vector2[] ClusterTarget(string targetName, int expectedCount, int clusterSize = 1) // ADD clusterSize parameter
	{
		var tileList = GetLocationsFromTilePattern(targetName);
		if (tileList is not { Count: > 0 })
		{
			return null;
		}

		// ADD THIS: Filter by cluster size before KMeans clustering
		if (clusterSize > 1)
		{
			tileList = FilterByClusterSize(tileList, clusterSize);
			if (tileList.Count == 0)
			{
				return null;
			}
		}

		var clusterIndexes = KMeans.Cluster(tileList.Select(x => new Vector2d(x.X, x.Y)).ToArray(), expectedCount);
		var resultList = new List<Vector2>();
        foreach (var tileGroup in tileList.Zip(clusterIndexes).GroupBy(x => x.Second))
        {
            var v = new Vector2();
            var count = 0;
            foreach (var (vector, _) in tileGroup)
            {
                var mult = IsGridWalkable(vector) ? 100 : 1;
                v += vector * mult;
                count += mult;
            }

            v /= count;
            var replacement = tileGroup.Select(tile => new Vector2i(tile.First.X, tile.First.Y))
                .Where(IsGridWalkable)
                .OrderBy(x => (x - v).LengthSquared())
                .Select(x => (Vector2i?)x)
                .FirstOrDefault();
            if (replacement != null)
            {
                v = replacement.Value;
            }

            if (!IsGridWalkable(v.Truncate()))
            {
                v = GetAllNeighborTiles(v.Truncate()).First(IsGridWalkable);
            }

            resultList.Add(v);
        }

        var locations = resultList.Distinct().ToArray();
        return locations;
    }

    private bool IsGridWalkable(Vector2i tile)
    {
        return _processedTerrainData[tile.Y][tile.X] is 5 or 4;
    }
    private List<Vector2i> DeduplicateNearbyMatches(List<Vector2i> matches, int minDistance)
    {
        var result = new List<Vector2i>();
        foreach (var match in matches)
        {
            if (!result.Any(existing =>
                Math.Abs(existing.X - match.X) < minDistance &&
                Math.Abs(existing.Y - match.Y) < minDistance))
            {
                result.Add(match);
            }
        }
        return result;
    }
    private List<Vector2i> FindPatternMatches(TargetDescription target)
    {
        if (target.ClusterPattern == null || target.ClusterPattern.Length == 0)
            return null;

        

        var matches = new List<Vector2i>();
        var pattern = target.ClusterPattern;
        var aliases = target.TileAliases ?? new Dictionary<string, string>();

       

        // CACHE tile data for performance - read once instead of millions of times
        var tileData = GameController.Memory.ReadStdVector<TileStructure>(_terrainMetadata.TgtArray);


        var rotations = new[]
{
    pattern,
    MirrorPattern(pattern),

    RotatePattern90(pattern),
    MirrorPattern(RotatePattern90(pattern)),

    RotatePattern90(RotatePattern90(pattern)),
    MirrorPattern(RotatePattern90(RotatePattern90(pattern))),

    RotatePattern90(RotatePattern90(RotatePattern90(pattern))),
    MirrorPattern(RotatePattern90(RotatePattern90(RotatePattern90(pattern))))
};

        // Calculate map dimensions in tiles
        int maxTileX = _areaDimensions.Value.X / TileToGridConversion;
        int maxTileY = _areaDimensions.Value.Y / TileToGridConversion;


        // Scan the entire map
        for (int tileY = 0; tileY < maxTileY; tileY++)
        {
            for (int tileX = 0; tileX < maxTileX; tileX++)
            {
                var gridPos = new Vector2i(tileX * TileToGridConversion, tileY * TileToGridConversion);

                // Check if any rotation matches at this position
                foreach (var rotatedPattern in rotations)
                {
                    if (PatternMatchesAt(gridPos, rotatedPattern, aliases, tileData))
                    {
                        // Calculate center position of the matched pattern
                        int patternWidth = rotatedPattern[0].Length;
                        int patternHeight = rotatedPattern.Length;
                        int centerX = gridPos.X + (patternWidth * TileToGridConversion) / 2;
                        int centerY = gridPos.Y + (patternHeight * TileToGridConversion) / 2;
                        matches.Add(new Vector2i(centerX, centerY));                       
                        break; // Don't count same position multiple times with different rotations
                    }
                }
            }
        }

        int dedupeDistance = Math.Max(pattern[0].Length, pattern.Length) * TileToGridConversion;
        matches = DeduplicateNearbyMatches(matches, dedupeDistance);
        return matches;
    }
    private readonly ConcurrentDictionary<string, Regex> _tilePatternCache = new();
    private bool TileMatchesPattern(string actualTile, string requiredTile)
    {
        if (!requiredTile.Contains('*'))
            return actualTile == requiredTile;

        var regex = _tilePatternCache.GetOrAdd(requiredTile, pattern =>
        {
            var escaped = Regex.Escape(pattern).Replace(@"\*", ".*");
            return new Regex($"^{escaped}$", RegexOptions.Compiled);
        });

        return actualTile != null && regex.IsMatch(actualTile);
    }
    /// <summary>
    /// Check if pattern matches at specific position
    /// </summary>
    /// 
    private bool PatternMatchesAt(Vector2i startPos, string[][] pattern, Dictionary<string, string> aliases, TileStructure[] tileData)
    {

        int patternHeight = pattern.Length;
        int patternWidth = pattern[0].Length;

        // Check each cell in the pattern
        for (int py = 0; py < patternHeight; py++)
        {
            for (int px = 0; px < patternWidth; px++)
            {
                var patternCell = pattern[py][px];
                var gridPos = new Vector2i(
                    startPos.X + px * TileToGridConversion,
                    startPos.Y + py * TileToGridConversion
                );

                // Skip wildcards (null or "*")
                if (patternCell == null || patternCell == "*")
                    continue;

                // Check for negation prefix "!"
                bool negate = patternCell.StartsWith("!");
                var cellKey = negate ? patternCell.Substring(1) : patternCell;

                // Resolve alias to full tile name
                var requiredTile = aliases.ContainsKey(cellKey)
                    ? aliases[cellKey]
                    : cellKey;

                // Get actual tile at this position
                var actualTile = GetTileAt(gridPos, tileData);

                // Check if tiles match, respecting negation
                bool matches = TileMatchesPattern(actualTile, requiredTile);
                if (negate ? matches : !matches)
                    return false;
            }
        }

        return true;
    }


    /// <summary>
    /// Rotate pattern 90 degrees clockwise
    /// </summary>
    private string[][] RotatePattern90(string[][] pattern)
    {
        int rows = pattern.Length;
        int cols = pattern[0].Length;
        var rotated = new string[cols][];

        for (int i = 0; i < cols; i++)
        {
            rotated[i] = new string[rows];
            for (int j = 0; j < rows; j++)
            {
                rotated[i][j] = pattern[rows - 1 - j][i];
            }
        }

        return rotated;
    }
    private string[][] MirrorPattern(string[][] pattern)
    {
        int rows = pattern.Length;
        int cols = pattern[0].Length;

        var mirrored = new string[rows][];

        for (int i = 0; i < rows; i++)
        {
            mirrored[i] = new string[cols];

            for (int j = 0; j < cols; j++)
            {
                mirrored[i][j] = pattern[i][cols - 1 - j];
            }
        }

        return mirrored;
    }


    /// <summary>
    /// Get tile name at grid position
    /// </summary>
    private string GetTileAt(Vector2i gridPos, TileStructure[] tileData)
    {
        // Convert grid position to tile coordinates
        int tileX = gridPos.X / TileToGridConversion;
        int tileY = gridPos.Y / TileToGridConversion;
        int tileIndex = tileY * _terrainMetadata.NumCols + tileX;

        // Bounds check
        if (tileIndex < 0 || tileX < 0 || tileY < 0 || tileX >= _terrainMetadata.NumCols)
            return null;

        try
        {
            if (tileIndex >= tileData.Length)
                return null;

            var tgtTileStruct = GameController.Memory.Read<TgtTileStruct>(tileData[tileIndex].TgtFilePtr);

            // Read the TgtPath (the .tdt file path) instead of the detail name
            var tilePath = tgtTileStruct.TgtPath.ToString(GameController.Memory);

            return tilePath;
        }
        catch (Exception ex)
        {
            
            return null;
        }
    }
    private IEnumerable<Vector2i> GetAllNeighborTiles(Vector2i start)
    {
        foreach (var range in Enumerable.Range(1, 100000))
        {
            var xStart = Math.Max(0, start.X - range);
            var yStart = Math.Max(0, start.Y - range);
            var xEnd = Math.Min(_areaDimensions.Value.X, start.X + range);
            var yEnd = Math.Min(_areaDimensions.Value.Y, start.Y + range);
            for (var x = xStart; x <= xEnd; x++)
            {
                yield return new Vector2i(x, yStart);
                yield return new Vector2i(x, yEnd);
            }

            for (var y = yStart + 1; y <= yEnd - 1; y++)
            {
                yield return new Vector2i(xStart, y);
                yield return new Vector2i(xEnd, y);
            }

            if (xStart == 0 && yStart == 0 && xEnd == _areaDimensions.Value.X && yEnd == _areaDimensions.Value.Y)
            {
                break;
            }
        }
    }
}