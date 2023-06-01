using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.IO.Compression;
using B3dm.Tile;
using B3dm.Tileset;
using CommandLine;
using Npgsql;
using Wkb2Gltf;
using System.Threading.Tasks;
using Konsole.Internal;

namespace pg2b3dm
{
    class Program
    {
        static string password = string.Empty;

        static object tilesLock = new Object();

        static void Main(string[] args)
        {
            var version = Assembly.GetEntryAssembly().GetName().Version;
            Console.WriteLine($"tool: pg2b3dm (tudelft3d fork) {version}");

            Parser.Default.ParseArguments<Options>(args).WithParsed(o => {
                o.User = string.IsNullOrEmpty(o.User) ? Environment.UserName : o.User;
                o.Database = string.IsNullOrEmpty(o.Database) ? Environment.UserName : o.Database;

                var connectionString = $"Host={o.Host};Username={o.User};Database={o.Database};Port={o.Port};Pooling=True;Command Timeout=300;passfile={o.PassFile}";
                var istrusted = TrustedConnectionChecker.HasTrustedConnection(connectionString);

                if (!istrusted && o.PassFile == "") {
                    Console.Write($"password for user {o.User}: ");
                    password = PasswordAsker.GetPassword();
                    connectionString += $";password={password}";
                    Console.WriteLine();
                }

                if (o.Compression != "" && o.Compression != "gzip")
                {
                    Console.WriteLine($"the entered compression type \"{o.Compression}\" is not supported, output will be uncompressed!");
                    o.Compression = "";
                }

                Console.WriteLine($"start processing....");

                var stopWatch = new Stopwatch();
                stopWatch.Start();

                var output = o.Output;
                var outputTiles = $"{output}{Path.DirectorySeparatorChar}tiles";
                if (!Directory.Exists(output)) {
                    Directory.CreateDirectory(output);
                }
                if (!Directory.Exists(outputTiles)) {
                    Directory.CreateDirectory(outputTiles);
                }

                Console.WriteLine($"input table:  {o.GeometryTable}");
                Console.WriteLine($"input geometry column:  {o.GeometryColumn}");

                Console.WriteLine($"output directory:  {outputTiles}");

                var geometryTable = o.GeometryTable;
                var geometryColumn = o.GeometryColumn;
                var QuadtreeTable = o.QuadtreeTable;
                var idcolumn = o.IdColumn;
                var lodcolumn = o.LodColumn;
                var tileIdColumn = o.TileIDColumn;
                var geometricErrors = Array.ConvertAll(o.GeometricErrors.Split(','), double.Parse); ;

                var conn = new NpgsqlConnection(connectionString);

                var lods = (lodcolumn != string.Empty ? LodsRepository.GetLods(conn, geometryTable, lodcolumn) : new List<int> { 0 });
                if((geometricErrors.Length != lods.Count + 1) && lodcolumn==string.Empty) {
                    Console.WriteLine($"lod levels: [{ String.Join(',', lods)}]");
                    Console.WriteLine($"geometric errors: {o.GeometricErrors}");

                    Console.WriteLine("error: parameter -g --geometricerrors is wrongly specified...");
                    Console.WriteLine("end of program...");
                    Environment.Exit(0);
                }
                if (lodcolumn != String.Empty){
                    Console.WriteLine($"lod levels: {String.Join(',', lods)}");

                    if (lods.Count >= geometricErrors.Length) {
                        Console.WriteLine($"calculating geometric errors starting from {geometricErrors[0]}");
                        geometricErrors = GeometricErrorCalculator.GetGeometricErrors(geometricErrors[0], lods);
                    }
                };
                Console.WriteLine("geometric errors: " + String.Join(',', geometricErrors));

                // We now need the bounding box of the quadtree (which equals the geometry of the root node), but it doesn't have z-value
                // Therefore, get the ZMin and ZMax from the table
                var bbox_qt = BoundingBoxRepository.GetBoundingBox3DForQT(conn, QuadtreeTable);
                var bbox_table = BoundingBoxRepository.GetBoundingBox3DForTable(conn, geometryTable, geometryColumn, QuadtreeTable);
                bbox_qt.ZMin = bbox_table.ZMin;
                bbox_qt.ZMax = bbox_table.ZMax;
                var bbox3d = bbox_qt;
                
                Console.WriteLine($"3D Boundingbox {geometryTable}.{geometryColumn}: [{bbox3d.XMin}, {bbox3d.YMin}, {bbox3d.ZMin},{bbox3d.XMax},{bbox3d.YMax}, {bbox3d.ZMax}]");
                var translation = bbox3d.GetCenter().ToVector();
                //  Console.WriteLine($"translation {geometryTable}.{geometryColumn}: [{string.Join(',', translation) }]");
                var boundingboxAllFeatures = BoundingBoxCalculator.TranslateRotateX(bbox3d, Reverse(translation), Math.PI / 2);
                var box = boundingboxAllFeatures.GetBox();

                // Increase root boundingVolume by the Z-transform, but don't use transform for tiles
                box[11] += translation[2];
                translation[2] = 0;

                var sr = SpatialReferenceRepository.GetSpatialReference(conn, geometryTable, geometryColumn);
                Console.WriteLine($"spatial reference: {sr}");
                Console.WriteLine($"reading quadtree...");
                var tiles = TileCutter.GetTiles(0, conn, o.ExtentTile, geometryTable, geometryColumn, bbox3d, sr, 0, lods, geometricErrors.Skip(1).ToArray(), QuadtreeTable, tileIdColumn, lodcolumn);
                Console.WriteLine();
                var leavesHeights = new Dictionary<String, (double, double)>();
                CalculateBoundingBoxes(conn, translation, tiles.tiles, leavesHeights, geometryTable, geometryColumn, sr);
                var nrOfTiles = RecursiveTileCounter.CountTiles(tiles.tiles, 0);
                Console.WriteLine($"tiles with features: {nrOfTiles} ");
                Console.WriteLine("writing tileset.json...");
                var json = TreeSerializer.ToJson(tiles.tiles, translation, box, geometricErrors[0], o.Refinement);
                File.WriteAllText($"{o.Output}/tileset.json", json);

                WriteTiles(connectionString, geometryTable, geometryColumn, idcolumn, translation, tiles.leaves, sr, o.Output, 0, nrOfTiles, o.skipHugeTiles, o.RoofColorColumn, o.AttributesColumn, o.LodColumn, o.SkipTiles, o.MaxThreads, o.Compression, o.DisablePb);

                stopWatch.Stop();
                Console.WriteLine();
                Console.WriteLine($"elapsed: {stopWatch.ElapsedMilliseconds / 1000} seconds");
                Console.WriteLine("program finished.");
            });
        }

        public static double[] Reverse(double[] translation)
        {
            var res = new double[] { translation[0] * -1, translation[1] * -1, translation[2] * -1 };
            return res;
        }
        private static Dictionary<String, (double, double)> getLeavesHeights( NpgsqlConnection conn, List<Tile> tiles, string geometry_table, string geometry_column, string tileIdColumn="tile_id" ) {

            void calculateLeavesHeights( Tile t, Dictionary<String, (double, double)> heights  ) {

                if ( !string.IsNullOrEmpty(t.Id) ) {

                    var sql = $"SELECT ST_ZMin(ST_3DExtent({ geometry_column })), ST_ZMax(ST_3DExtent({ geometry_column })) FROM { geometry_table } WHERE { tileIdColumn }='{ t.Id }'";
                    var cmd = new NpgsqlCommand(sql, conn);
                    var reader = cmd.ExecuteReader();
                    reader.Read();
                    var minZ = reader.IsDBNull(0) ? 0 : reader.GetDouble(0);
                    var maxZ = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                    reader.Close();

                    heights[ t.Id.ToString() ] = ( minZ, maxZ );

                } else if ( t.Children != null ) {

                    foreach ( var c in t.Children ) {
                        calculateLeavesHeights( c, heights );
                    }

                }

            }

            var tileHeights = new Dictionary<String, (double, double)>();

            foreach ( var t in tiles ) {

                calculateLeavesHeights( t, tileHeights );

            }

            return tileHeights;

        }

        private static void CalculateBoundingBoxes(NpgsqlConnection conn, double[] translation, List<Tile> tiles, Dictionary<String, (double, double)> leavesHeights, string geometry_table, string geometry_column, int epsg)
        {

            void getChildrenExtent( Tile t, BoundingBox3D bbox ) {

                // Only leafs, since nodes might not yet have a proper bbox
                if ( !string.IsNullOrEmpty(t.Id) ) {

                    if (t.BoundingBox.XMin < bbox.XMin) {
                        bbox.XMin = t.BoundingBox.XMin;
                    }
                    if (t.BoundingBox.YMin < bbox.YMin) {
                        bbox.YMin = t.BoundingBox.YMin;
                    }
                    if (t.BoundingBox.ZMin < bbox.ZMin) {
                        bbox.ZMin = t.BoundingBox.ZMin;
                    }
                    if (t.BoundingBox.XMax > bbox.XMax) {
                        bbox.XMax = t.BoundingBox.XMax;
                    }
                    if (t.BoundingBox.YMax > bbox.YMax) {
                        bbox.YMax = t.BoundingBox.YMax;
                    }
                    if (t.BoundingBox.ZMax > bbox.ZMax) {
                        bbox.ZMax = t.BoundingBox.ZMax;
                    }

                }



                if ( t.Children != null ) {
                    foreach ( var c in t.Children ) {

                        getChildrenExtent( c, bbox );
                    }
                }

            }

            foreach (var t in tiles) {

                var bb = t.BoundingBox;
                var tid = t.Id;
                var childrenIds = new List<string>();

                var bbox = new BoundingBox3D(double.MaxValue, double.MaxValue, double.MaxValue, double.MinValue, double.MinValue, double.MinValue);

                getChildrenExtent( t, bbox );

                t.BoundingBox = bbox;
                var bvolRotated = BoundingBoxCalculator.TranslateRotateX(t.BoundingBox, Reverse(translation), Math.PI / 2);

                if (t.Children != null) {

                    CalculateBoundingBoxes(conn, translation, t.Children, leavesHeights, geometry_table, geometry_column, epsg);

                }
                t.Boundingvolume = TileCutter.GetBoundingvolume(bvolRotated);
            }

        }

        private static int WriteTiles(string connectionString, string geometryTable, string geometryColumn, string idcolumn, double[] translation, List<Tile> tiles, int epsg, string outputPath, int counter, int maxcount, int skipHugeTiles, string colorColumn = "", string attributesColumn = "", string lodColumn="", bool SkipTiles=false, int MaxThreads=-1, string compressionType="", bool DisablePb=false)
        
        {   

            object counterLock = new object();
            // counter = 0;    

            var options = new ParallelOptions();
            options.MaxDegreeOfParallelism = MaxThreads;

            var pb = DisablePb ? null : new Konsole.ProgressBar(Konsole.PbStyle.SingleLine, maxcount);

            if (!DisablePb) {
                pb.Refresh(counter, "Starting...");
            } else {
                Console.WriteLine("Starting...");
            }

            var skippedTiles = new List<string>();

            Parallel.For(0, tiles.Count,
            options,
            () => {
                var new_conn = new NpgsqlConnection(connectionString);

                return new_conn;
            },
            (int c, ParallelLoopState state, NpgsqlConnection new_conn) => {
                var t = tiles[c];
                lock (counterLock)
                {
                    counter++;
                    var perc = Math.Round(((double)counter / maxcount) * 100, 2);
                    if (!DisablePb) {
                        pb.Refresh(counter, $"{counter}/{maxcount} - {perc:F}%");
                    } else {
                        Console.Write($"\rcreating tiles: {counter}/{maxcount} - {perc:F}%");
                    }
                }

                var compressionExtension = "";
                if ( compressionType == "gzip" )
                    compressionExtension = ".gz";
                var filename = $"{outputPath}/tiles/{t.Id.Replace('/', '-')}.b3dm" + compressionExtension;
                if (SkipTiles && File.Exists(filename))
                {
                    return new_conn;
                }

                var geometries = BoundingBoxRepository.GetGeometrySubset(new_conn, geometryTable, geometryColumn, idcolumn, translation, t, epsg, colorColumn, attributesColumn, lodColumn);

                var triangleCollection = GetTriangles(geometries);

                if ( skipHugeTiles != 0 && triangleCollection.Count > skipHugeTiles ) {
                    System.Console.WriteLine("Tile {0} has {1} triangles and is skipped", t.Id, triangleCollection.Count);
                    skippedTiles.Add(t.Id.ToString());
                    return new_conn;
                }

                var attributes = GetAttributes(geometries);

                var b3dm = B3dmCreator.GetB3dm(attributesColumn, attributes, triangleCollection);

                var bytes = b3dm.ToBytes();

                if (compressionType == "")
                {
                    File.WriteAllBytes(filename, bytes);
                }
                else if (compressionType == "gzip")
                {
                    using (FileStream fileToCompress = File.Create(filename)) 
                    {
                        using (GZipStream compressionStream = new GZipStream(fileToCompress, CompressionMode.Compress)) 
                        {
                            compressionStream.Write(bytes, 0, bytes.Length);
                        }
                    }
                }

                if (t.Children != null) {
                    counter = WriteTiles(connectionString, geometryTable, geometryColumn, idcolumn, translation, t.Children, epsg, outputPath, counter, maxcount, skipHugeTiles, colorColumn, attributesColumn, lodColumn, SkipTiles, MaxThreads, compressionType, DisablePb);
                }

                return new_conn;
            },
            (NpgsqlConnection new_conn) => {
                new_conn.Close();
            }); 

            File.WriteAllLines(outputPath + "/skippedtiles.txt", skippedTiles);

            if (!DisablePb) {
                Console.WriteLine("\nAaaand... done!");
            }
            if ( skippedTiles.Count > 0 ) {
                System.Console.WriteLine("Some tiles have been skipped because they were too big. See \"skippedtiles.txt\".");
            }

            return counter;
        }

        private static List<object> GetAttributes(List<GeometryRecord> geometries)
        {
            var allattributes = new List<object>();
            foreach (var geom in geometries) {
                if (geom.Attributes.Length > 0) {
                    // only take the first now....
                    allattributes.Add(geom.Attributes[0]);
                }
            }
            return allattributes;
        }

        public static List<Wkb2Gltf.Triangle> GetTriangles(List<GeometryRecord> geomrecords)
        {
            var triangleCollection = new List<Wkb2Gltf.Triangle>();
            foreach (var g in geomrecords) {
                var triangles = g.GetTriangles();
                triangleCollection.AddRange(triangles);
            }

            return triangleCollection;
        }

    }
}
