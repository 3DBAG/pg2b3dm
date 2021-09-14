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

        public static (int tileId, List<Tile> tiles, List<Tile> leaves) GetTiles(int tileId, NpgsqlConnection conn, double extentTile, string geometryTable, string geometryColumn, BoundingBox3D box3d, int epsg, int currentLod, List<int> lods, double[] geometricErrors, string quadtree_table, string leaves_table, string tileIdColumn, string lodcolumn = "")
        {
            // "tiles" to create the tileset with, "leaves" to give to the "writeTiles" function (this flat list helps for multithreading).
            var tiles = new List<Tile>();
            var leaves = new List<Tile>();

            var used_nodes_indices = new List<Dictionary<String, (String, Tile)>>();
            var used_nodes = new List<Dictionary<String, (String, Tile)>>();
            var leaf_nodes = new Dictionary<String, (String, Tile)>();

            // Standard geometric error (don't set to 0, it will make 3DTilesRendererJS skip the tiles)
            var error = 500;

            var sql1 = $"SELECT MAX(z) FROM {quadtree_table};";
            var sql2 = $"SELECT f.pid, f.id, l.tile_id AS b3dm_id, ST_AsBinary(l.tile_polygon) AS leaf_geom, b.min AS min, b.max AS max FROM {quadtree_table} AS f, {leaves_table} AS l INNER JOIN ( SELECT tile_id, ST_Xmin(bbox) as bbox, ST_AsBinary(ST_MakePoint(ST_XMin(bbox), ST_YMin(bbox), ST_ZMin(bbox))) as min, ST_AsBinary(ST_MakePoint(ST_XMax(bbox), ST_YMax(bbox), ST_ZMax(bbox))) as max FROM (SELECT tile_id, ST_3DExtent({geometryColumn}) as bbox FROM {geometryTable} GROUP BY {tileIdColumn}) as bbox ) AS b ON (l.{tileIdColumn} = b.{tileIdColumn}) WHERE ST_Intersects(l.tile_polygon, f.geom) AND (ST_Equals(l.tile_polygon, f.geom))";
            conn.Open();
            var cmd = new NpgsqlCommand(sql1, conn);
            var reader = cmd.ExecuteReader();
            reader.Read();

            // Init nested lists with amount of tile levels
            var max_z = reader.GetInt32(0);

            foreach (int i in Enumerable.Range(0, max_z + 1)) {
                used_nodes_indices.Add(new Dictionary<String, (String, Tile)>());
                used_nodes.Add(new Dictionary<String, (String, Tile)>());
            }
                
            reader.Close();

            // Store all leaves as tiles and keep the indices of their parents. Query matches leaves with quadtree nodes
            cmd = new NpgsqlCommand(sql2, conn);
            reader = cmd.ExecuteReader();
            while (reader.Read()) {

                var parent_id = reader.GetString(0);
                var node_id = reader.GetString(1);
                var b3dm_id = reader.GetString(2);
                var min_stream = reader.GetStream(4);
                var min = Geometry.Deserialize<WkbSerializer>(min_stream).GetCenter();
                min_stream.Close();
                var max_stream = reader.GetStream(5);
                var max = Geometry.Deserialize<WkbSerializer>(max_stream).GetCenter();
                max_stream.Close();
                
                string[] parent_ids = parent_id.Split('-');
                List<string> p_ids = new List<string>(parent_ids);
                
                var parent_levels = parent_ids.Length;
                
                foreach (int level in Enumerable.Range(0, parent_levels).Reverse()) {
                    
                    var id = String.Join("-", p_ids.GetRange(0, level+1));

                    if ( !used_nodes_indices[level].ContainsKey(id) ) {

                        used_nodes_indices[level].Add(id, (null, null));
                        
                    }

                }

                var tile = new Tile(Int32.Parse(b3dm_id), new BoundingBox3D(min.X.Value, min.Y.Value, min.Z.Value, max.X.Value, max.Y.Value, max.Z.Value)) {
                    Lod = 0,
                    GeometricError = error
                };

                leaf_nodes[node_id] = (parent_id, tile);
                leaves.Add(tile);

            }

            reader.Close();

            // Create tiles for all non-leaf nodes that are used
            var level_i = 0;
            foreach ( var level in used_nodes_indices ) {

                List<string> keys = new List<string>(level.Keys);
                string keys_str = string.Format("'{0}'", string.Join("','", keys));

                var sql3 = $"SELECT id, pid FROM {quadtree_table} WHERE id in ({keys_str})";
                cmd = new NpgsqlCommand(sql3, conn);
                reader = cmd.ExecuteReader();
                while ( reader.Read() ) {

                    var id = reader.GetString(0);
                    var pid = reader.GetString(1);

                    var tile = new Tile(0, new BoundingBox3D(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)) {
                        Lod = 0,
                        GeometricError = error,
                        Children = new List<Tile>()
                    };

                    used_nodes[level_i][id] = (pid, tile);

                }

                level_i += 1;

                reader.Close();

            }

            // Add leaves as child to their parent node
            foreach ( KeyValuePair<String, (String, Tile)> leaf in leaf_nodes ) {

                var pid = leaf.Value.Item1;
                var tile = leaf.Value.Item2;
                var parent_level = pid.Split('-').Length - 1;
                used_nodes[parent_level][pid].Item2.Children.Add(tile);

            }

            // Link all nodes bottom-up
            foreach ( var i in Enumerable.Range(1, used_nodes.Count - 1).Reverse() ) {
                foreach ( KeyValuePair<String, (String, Tile)> entry in used_nodes[i] ) {

                    var node = entry.Value;
                    var plevel = i - 1;
                    used_nodes[plevel][node.Item1].Item2.Children.Add(node.Item2);

                }
            }

            // Add tiles of tile level 1 to output (omitting the root tile)
            foreach (KeyValuePair<String, (String, Tile)> entry in used_nodes[1]) {
                tiles.Add(entry.Value.Item2);
            }

            conn.Close();
            return (counter, tiles, leaves);
            
        }

    }

}
