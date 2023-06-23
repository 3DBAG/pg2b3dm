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

        public static (int tileId, List<Tile> tiles, List<Tile> leaves) GetTiles(int tileId, NpgsqlConnection conn, double extentTile, string geometryTable, string geometryColumn, BoundingBox3D box3d, int epsg, int currentLod, double[] geometricErrors, string quadtree_table, string tileIdColumn, string lodcolumn = "")
        {
            // "tiles" to create the tileset with, "leaves" to give to the "writeTiles" function (this flat list helps for multithreading).
            var tiles = new List<Tile>();
            var leaves = new List<Tile>();

            var used_nodes_indices = new List<Dictionary<String, (String, Tile)>>();
            var used_nodes = new List<Dictionary<String, (String, Tile)>>();
            var leaf_nodes = new Dictionary<String, (String, Tile)>();

            // Standard geometric error (don't set to 0, it will make 3DTilesRendererJS skip the tiles)
            var error = 500;

            var sql1 = $"SELECT MAX(level) FROM {quadtree_table} WHERE NOT leaf;";             
            var sql2 = $@"SELECT leaves.id, leaves.parents, 
                                  b.min AS min, b.max AS max 
                          FROM (SELECT c.id as id, array_agg(p.id ORDER by p.id DESC) as parents
                                FROM {quadtree_table} c
                                INNER JOIN {quadtree_table} p 
                                ON c.level != p.level 
                                AND ST_within(c.geom, p.geom)
                                WHERE c.leaf
                                GROUP BY c.id) as leaves
                          INNER JOIN (SELECT tile_id,
                                        ST_AsBinary(
                                            st_setsrid(
                                            ST_MakePoint(
                                                ST_XMin(bbox), ST_YMin(bbox), ST_ZMin(bbox)
                                                )
                                            , 28992)
                                        ) as min,
                                        ST_AsBinary(st_setsrid(
                                            ST_MakePoint(ST_XMax(bbox), ST_YMax(bbox), ST_ZMax(bbox)), 28992)) as max 
                                      FROM (
                                                SELECT {tileIdColumn} as tile_id, ST_3DExtent({geometryColumn}) as bbox 
                                                FROM {geometryTable}  
                                                WHERE {lodcolumn} = {currentLod}
                                                GROUP BY {tileIdColumn}
                                            ) as bbox 
                                        ) as b
                          ON leaves.id = b.{tileIdColumn}";
            
            conn.Open();
            var cmd = new NpgsqlCommand(sql1, conn);
            var reader = cmd.ExecuteReader();
            reader.Read();
            var max_z = reader.GetInt32(0);
            reader.Close();


            // Init nested lists with amount of tile levels
            foreach (int i in Enumerable.Range(0, max_z + 1)) {
                used_nodes_indices.Add(new Dictionary<String, (String, Tile)>());
                used_nodes.Add(new Dictionary<String, (String, Tile)>());
            }
                
            // Store all leaves as tiles and keep the indices of their parents. Query matches leaves with quadtree nodes
            cmd = new NpgsqlCommand(sql2, conn);
            reader = cmd.ExecuteReader();
            while (reader.Read()) {

                var node_id = reader.GetString(0);
                var b3dm_id = reader.GetString(0);
                string[] parents = (String[])reader.GetValue(1);
                var first_parent_id = parents[0];
                var min_stream = reader.GetStream(2);
                var min = Geometry.Deserialize<WkbSerializer>(min_stream).GetCenter();
                min_stream.Close();
                var max_stream = reader.GetStream(3);
                var max = Geometry.Deserialize<WkbSerializer>(max_stream).GetCenter();
                max_stream.Close();
                
                foreach (string parent_id in parents) {
                    var parent_level = Int32.Parse(parent_id.Split('/')[0]);

                    if ( !used_nodes_indices[parent_level].ContainsKey(parent_id) ) {
                        used_nodes_indices[parent_level].Add(parent_id, (null, null));
                    }
                }

                var tile = new Tile(b3dm_id, new BoundingBox3D(min.X.Value, min.Y.Value, min.Z.Value, max.X.Value, max.Y.Value, max.Z.Value)) {
                    Lod = currentLod,
                    GeometricError = error
                };

                leaf_nodes[node_id] = (first_parent_id, tile);
                leaves.Add(tile);

            }

            reader.Close();

            // Create tiles for all non-leaf nodes that are used
            var level_i = 0;
            foreach ( var level in used_nodes_indices ) {

                List<string> keys = new List<string>(level.Keys);
                string keys_str = string.Format("'{0}'", string.Join("','", keys));

                var sql3 = $@"SELECT c.id AS id, p.id AS p_id
                              FROM {quadtree_table} c 
                              LEFT JOIN {quadtree_table} p
                              ON c.level = p.level+1 
                              AND ST_within(c.geom, p.geom)
                              WHERE c.id in ({keys_str})";
                cmd = new NpgsqlCommand(sql3, conn);
                reader = cmd.ExecuteReader();
                while ( reader.Read() ) {

                    var id = reader.GetString(0);
                    var pid = "";
                    if (level_i !=0){ // if level is 0 then there is on parent (node '0/0/0')
                        pid = reader.GetString(1);
                    }

                    var tile = new Tile("", new BoundingBox3D(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)) {
                        Lod = currentLod,
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
                var parent_level = Int32.Parse(pid.Split('/')[0]);
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
