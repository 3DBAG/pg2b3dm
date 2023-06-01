﻿using System.Collections.Generic;
using Newtonsoft.Json;

namespace B3dm.Tileset
{
    public static class TreeSerializer
    {

        public static string ToJson(List<Tile> tiles, double[] transform, double[] box, double maxGeometricError, string refinement)
        {
            var tileset = ToTileset(tiles, transform, box, maxGeometricError, refinement);
            var json = JsonConvert.SerializeObject(tileset, new JsonSerializerSettings() { NullValueHandling = NullValueHandling.Ignore });
            return json; ;
        }

        public static TileSet ToTileset(List<Tile> tiles, double[] transform, double[] box, double maxGeometricError, string refinement)
        {
            var geometricError = maxGeometricError;
            var tileset = new TileSet {
                asset = new Asset() { version = "1.0", generator = "pg2b3dm" }
            };
            var t = new double[] { 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, transform[0], transform[1], transform[2], 1.0 };
            tileset.geometricError = geometricError;
            var root = GetRoot(tiles, geometricError, t, box, refinement);
            tileset.root = root;
            return tileset;
        }

        private static Root GetRoot(List<Tile> tiles, double geometricError, double[] translation, double[] box, string refinement)
        {
            var boundingVolume = new Boundingvolume {
                box = box
            };

            var root = new Root {
                geometricError = geometricError,
                refine = refinement,
                transform = translation,
                boundingVolume = boundingVolume
            };
            var children = GetChildren(tiles);
            root.children = children;
            return root;
        }

        private static List<Child> GetChildren(List<Tile> tiles)
        {
            var children = new List<Child>();
            foreach (var tile in tiles) {
                var child = GetChild(tile);

                if (tile.Children != null) {
                    child.children = GetChildren(tile.Children);
                }
                children.Add(child);
            }

            return children;
        }

        public static Child GetChild(Tile tile)
        {
            var child = new Child {
                geometricError = tile.GeometricError
            };
            child.boundingVolume = tile.Boundingvolume;
            // Tile IDs of nodes are 0
            if ( !string.IsNullOrEmpty(tile.Id) ) {
                child.content = new Content();
                child.content.uri = $"tiles/{tile.Id.Replace('/','-')}.b3dm";
            }
            return child;
        }
    }
}
