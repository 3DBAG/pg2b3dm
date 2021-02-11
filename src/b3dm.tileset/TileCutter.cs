using System;
using System.Collections.Generic;
using Npgsql;
using Wkx;
using System.IO;
using System.Linq;

namespace B3dm.Tileset
{
    public static class TileCutter
    {
        private static int counter = 0;
        public static Boundingvolume GetBoundingvolume(BoundingBox3D bbox3d)
        {
            var boundingVolume = new Boundingvolume {
                box = bbox3d.GetBox()
            };
            return boundingVolume;
        }

        public static byte[] StringToByteArray(String hex)
        {
            int NumberChars = hex.Length;
            byte[] bytes = new byte[NumberChars / 2];
            for (int i = 0; i < NumberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }

        public static (int tileId, List<Tile> tiles) GetTiles(int tileId, NpgsqlConnection conn, double extentTile, string geometryTable, string geometryColumn, BoundingBox3D box3d, int epsg, int currentLod, List<int> lods, double[] geometricErrors, string lodcolumn = "", string qtPath = "")
        {
            var tiles = new List<Tile>();

            if (qtPath.Length == 0) { 

                var xrange = (int)Math.Ceiling(box3d.ExtentX() / extentTile);
                var yrange = (int)Math.Ceiling(box3d.ExtentY() / extentTile);

                Console.WriteLine($"Extent is: {extentTile}");

                for (var x = 0; x < xrange; x++) {
                    for (var y = 0; y < yrange; y++) {
                        if (currentLod == 0) {
                            counter++;
                            var perc = Math.Round((double)counter / (xrange*yrange) * 100, 2);
                            Console.Write($"\rcreating quadtree: {counter}/{xrange * yrange} - {perc:F}%");
                        }

                        var lodQuery = LodQuery.GetLodQuery(lodcolumn, lods[currentLod]);
                        var from = new Point(box3d.XMin + extentTile * x, box3d.YMin + extentTile * y);
                        var to = new Point(box3d.XMin + extentTile * (x + 1), box3d.YMin + extentTile * (y + 1));
                        var hasFeatures = true; // BoundingBoxRepository.HasFeaturesInBox(conn, geometryTable, geometryColumn, from, to, epsg, lodQuery);
                        if (hasFeatures) {
                            tileId++;
                            var tile = new Tile(tileId, new BoundingBox((double)from.X, (double)from.Y, (double)to.X, (double)to.Y)) {
                                Lod = lods[currentLod],
                                GeometricError = geometricErrors[currentLod]
                            };
                            if (currentLod < lods.Count - 1) {
                                var newBox3d = new BoundingBox3D((double)from.X, (double)from.Y, (double)box3d.FromPoint().Z, (double)to.X, (double)to.Y, (double)box3d.ToPoint().Z);
                                var new_tiles = GetTiles(tileId, conn, extentTile / 2, geometryTable, geometryColumn, newBox3d, epsg, currentLod + 1, lods, geometricErrors, lodcolumn);
                                tile.Children = new_tiles.tiles;
                                tileId = new_tiles.tileId;
                            }
                            tiles.Add(tile);
                        }
                    }
                }
                return (tileId, tiles);

            } else {

                var used_nodes = new List<SortedSet<int>>();

                // Max z (= amount of tile levels)
                var sql1 = "SELECT MAX(z) FROM quadtree_full;";
                // Matches leaves with quadtree nodes
                var sql2 = "SELECT f.*, l.tile_id AS b3dm_id, ST_AsBinary(l.tile_polygon) AS leaf_geom FROM quadtree_full AS f, quadtree_leaves AS l WHERE ST_Intersects(l.tile_polygon, f.geom) AND (ST_Equals(l.tile_polygon, f.geom))";
                conn.Open();

                var cmd = new NpgsqlCommand(sql1, conn);
                var reader = cmd.ExecuteReader();
                while (reader.Read()) {
                    var max_z = reader.GetInt32(0);

                    foreach (int i in Enumerable.Range(0, max_z + 1)) {
                        used_nodes.Add(new SortedSet<int>());
                    }
                    
                }
                reader.Close();


                cmd = new NpgsqlCommand(sql2, conn);
                reader = cmd.ExecuteReader();
                while (reader.Read()) {

                    var parent_id = reader.GetString(0);
                    var node_id = reader.GetString(1);
                    var node_x = reader.GetInt32(2);
                    var node_y = reader.GetInt32(3);
                    var node_z = reader.GetInt32(4);
                    var b3dm_id = reader.GetString(6);
                    var geom_stream = reader.GetStream(7);
                    
                    var level = 0;
                    foreach (string i in node_id.Split('-')) {
                    
                        used_nodes[level].Add(Int32.Parse(i));

                        level += 1;

                    }

                    var leaf_geom = Geometry.Deserialize<WkbSerializer>(geom_stream);
                    var bbox = leaf_geom.GetBoundingBox();
                    var from = new Point(bbox.XMin, bbox.YMin);
                    var to = new Point(bbox.XMax, bbox.YMax);

                    var tile = new Tile(Int32.Parse(b3dm_id), new BoundingBox((double)from.X, (double)from.Y, (double)to.X, (double)to.Y)) {
                        Lod = 0,
                        GeometricError = 0
                    };

                    tiles.Add(tile);

                }

                reader.Close();
                conn.Close();

                return (counter, tiles);

            }
        }

    }
}
