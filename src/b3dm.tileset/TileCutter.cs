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

        public static (int tileId, List<Tile> tiles, List<Tile> leaves) GetTiles(int tileId, NpgsqlConnection conn, double extentTile, string geometryTable, string geometryColumn, BoundingBox3D box3d, int epsg, int currentLod, List<int> lods, double[] geometricErrors, string quadtree_table, string leaves_table, string lodcolumn = "")
        {
            var tiles = new List<Tile>();
            var leaves = new List<Tile>();

            var used_nodes_indices = new List<Dictionary<String, (String, Tile)>>();
            var used_nodes = new List<Dictionary<String, (String, Tile)>>();

            var leaf_tiles = new Dictionary<String, (String, Tile)>();

            // Max z (= amount of tile levels)
            var sql1 = $"SELECT MAX(z) FROM {quadtree_table};";
            // Matches leaves with quadtree nodes
            var sql2 = $"SELECT f.*, l.tile_id AS b3dm_id, ST_AsBinary(l.tile_polygon) AS leaf_geom FROM {quadtree_table} AS f, {leaves_table} AS l WHERE ST_Intersects(l.tile_polygon, f.geom) AND (ST_Equals(l.tile_polygon, f.geom))";
            conn.Open();

            var cmd = new NpgsqlCommand(sql1, conn);
            var reader = cmd.ExecuteReader();

            while (reader.Read()) {
                var max_z = reader.GetInt32(0);

                foreach (int i in Enumerable.Range(0, max_z + 1)) {
                    used_nodes_indices.Add(new Dictionary<String, (String, Tile)>());
                    used_nodes.Add(new Dictionary<String, (String, Tile)>());
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

                
                string[] parent_ids = parent_id.Split('-');
                List<string> p_ids = new List<string>(parent_ids);
                
                var parent_levels = parent_ids.Length;
                
                
                foreach (int level in Enumerable.Range(0, parent_levels).Reverse()) {
                    
                    var id = String.Join("-", p_ids.GetRange(0, level+1));

                    if ( !used_nodes_indices[level].ContainsKey(id) ) {
                        used_nodes_indices[level].Add(id, (null, null));
                    }

                }

                var leaf_geom = Geometry.Deserialize<WkbSerializer>(geom_stream);
                var bbox = leaf_geom.GetBoundingBox();
                var from = new Point(bbox.XMin, bbox.YMin);
                var to = new Point(bbox.XMax, bbox.YMax);

                var tile = new Tile(Int32.Parse(b3dm_id), new BoundingBox((double)from.X, (double)from.Y, (double)to.X, (double)to.Y)) {
                    Lod = 0,
                    GeometricError = 500
                };

                leaf_tiles[node_id] = (parent_id, tile);
                leaves.Add(tile);

            }

            reader.Close();

            var level_i = 0;
            foreach ( var level in used_nodes_indices ) {

                List<string> keys = new List<string>(level.Keys);

                string keys_str = string.Format("'{0}'", string.Join("','", keys));
                // Console.WriteLine(keys_str);

                var sql3 = $"SELECT id, pid, ST_AsBinary(geom) FROM {quadtree_table} WHERE id in ({keys_str})";

                cmd = new NpgsqlCommand(sql3, conn);
                reader = cmd.ExecuteReader();
                while ( reader.Read() ) {

                    var id = reader.GetString(0);
                    var pid = reader.GetString(1);
                    var geom_stream = reader.GetStream(2);
                    var node_geom = Geometry.Deserialize<WkbSerializer>(geom_stream);

                    var bbox = node_geom.GetBoundingBox();
                    var from = new Point(bbox.XMin, bbox.YMin);
                    var to = new Point(bbox.XMax, bbox.YMax);

                    var tile = new Tile(0, new BoundingBox((double)from.X, (double)from.Y, (double)to.X, (double)to.Y)) {
                        Lod = 0,
                        GeometricError = 500,
                        Children = new List<Tile>()
                    };

                    used_nodes[level_i][id] = (pid, tile);

                }

                level_i += 1;

                reader.Close();

            }

            foreach ( KeyValuePair<String, (String, Tile)> leaf in leaf_tiles ) {

                var pid = leaf.Value.Item1;
                var tile = leaf.Value.Item2;
                var parent_level = pid.Split('-').Length - 1;
                used_nodes[parent_level][pid].Item2.Children.Add(tile);

            }

            foreach ( var i in Enumerable.Range(1, used_nodes.Count - 1).Reverse() ) {
                foreach ( KeyValuePair<String, (String, Tile)> entry in used_nodes[i] ) {

                    var node = entry.Value;
                    var plevel = i - 1;
                    used_nodes[plevel][node.Item1].Item2.Children.Add(node.Item2);

                }
            }

            Console.WriteLine(used_nodes[0]["0"].Item2.Children.Count);
            tiles.Add(used_nodes[0]["0"].Item2);

            conn.Close();
            return (counter, tiles, leaves);

            
        }

    }
}
