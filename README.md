# pg2b3dm


This tool has originally been forked from [Geodan/pg2b3dm](https://github.com/Geodan/pg2b3dm) but has been modified in order to accommodate the needs of the 3DBAG pipeline. The modifications include multithreading, the use of a custom quadtree (stored in a database) and gzip compression. It is being used for the generation of 3D tiles for the 3DBAG.


 Prerequisite: [.NET 6.0 SDK](https://dotnet.microsoft.com/download/dotnet/6.0) should be installed installed

 ## How to create the Quadtree table:

 Assuming that the quadtree is available in .tsv format (usually in /data/3DBAG/export/ on gilfoyle) first create the table in the `baseregisters` DB (drop it if it already exists):

 ```SQL
 -- DROP TABLE tiles.quadtree;
CREATE TABLE tiles.quadtree(
   id        varchar(30)  NOT NULL,
   level     int          NOT NULL,
   nr_items  int          NOT NULL,
   leaf      bool         NOT NULL,
   geom      TEXT         NOT NULL,
CONSTRAINT id PRIMARY KEY (id));
```

You can import the file by connecting to the `baseregisters` database from Gilfoyle and running:
```SQL
\COPY tiles.quadtree 
FROM '/data/3DBAG/export/quadtree.tsv'
DELIMITER E'\t'
CSV HEADER;
```

And finally modify the Geometry column:

```SQL
ALTER TABLE tiles.quadtree
ALTER COLUMN geom TYPE GEOMETRY(POLYGON, 28992)
USING ST_GeomFromText(geom, 28992);
```

##  How to create the gpkg table:

On Gilfoyle, gather the paths of the triangulated gpkg files in a single file:

```bash
find -L /data/3DBAG/export/tiles/ -path "/data/3DBAG/export/tiles/*tri.gpkg" > all_gpkg.txt
```

In the `baseregisters` database first drop the old `tiles.gpkg_files` table and then import the .gpkg files:

```bash
export PG_USE_COPY=TRUE
 while read f; do
   base_name=$(basename ${f})
   echo ${base_name}
   names=($(echo ${base_name} | sed s/-/\\n/g))
   id=${names[0]}/${names[1]}/${names[2]}
   lod=${names[3]: -2}
    ogr2ogr -update -append  -f "PostgreSQL" PG:"host=localhost user=<USERNAME> dbname=baseregisters password=<PASSWORD>" $f -nlt MULTIPOLYGON25D -nln tiles.gpkg_files -sql """SELECT '$base_name' AS filename, ${names[0]} AS level,  '$id' AS tile_id, ${lod} AS lod, * FROM geom""" -gt 65536 -lco SPATIAL_INDEX=NO
done < all_gpkg.txt
```

After importing you need to  1) create the attributes column (**make sure you include any new attributes**), 2) correct the Z dimension of geometries by subtracting the ground height, 3) create the indexes and 4) delete features with dimensions with edge values.

```SQL
ALTER TABLE tiles.gpkg_files ADD COLUMN attributes text;
UPDATE tiles.gpkg_files SET attributes  = ROW_TO_JSON(
(SELECT d
  FROM (
    SELECT 
    "identificatie", 
		"status", 
		"oorspronkelijkbouwjaar", 
		"b3_h_maaiveld", 
		"b3_volume_lod12",
		"b3_volume_lod13", 
		"b3_volume_lod22", 
		"b3_dak_type", 
		"b3_pw_datum", 
		"b3_pw_bron",
    "b3_pw_onvoldoende",
    "b3_pw_selectie_reden", 
		"b3_kas_warenhuis", 
		"b3_reconstructie_onvolledig", 
		"b3_val3dity_lod12",
		"b3_val3dity_lod13", 
		"b3_val3dity_lod22",
		"b3_rmse_lod12",
		"b3_rmse_lod13",
		"b3_rmse_lod22",
		"b3_mutatie_ahn3_ahn4",
    "b3_mutatie_ahn4_ahn5",
		"b3_nodata_fractie_ahn3", 
		"b3_nodata_fractie_ahn4", 
    "b3_nodata_fractie_ahn5", 
		"b3_nodata_radius_ahn3", 
		"b3_nodata_radius_ahn4",
    "b3_nodata_radius_ahn5", 
		"b3_puntdichtheid_ahn3", 
		"b3_puntdichtheid_ahn4",
    "b3_puntdichtheid_ahn5",
		"b3_opp_buitenmuur",
		"b3_opp_dak_plat",
		"b3_opp_dak_schuin",
		"b3_opp_grond",
		"b3_opp_scheidingsmuur",
		"b3_bouwlagen",
		"b3_kwaliteitsindicator",
    "b3_extrusie",
    "b3_is_glas_dak",
    "b3_n_vlakken",
    "b3_succes",
    "b3_t_run",
    ) d))::text;

UPDATE tiles.gpkg_files SET geom = ST_Translate(geom, 0, 0, "b3_h_maaiveld" * -1.0); 

CREATE INDEX IF NOT EXISTS gpkg_files_tile_id_idx
ON tiles.gpkg_files
USING btree(tile_id);
           
CREATE INDEX IF NOT EXISTS gpkg_files_geom_idx
ON tiles.gpkg_files 
USING gist(geom); 

DELETE FROM tiles.gpkg_files a 
WHERE (st_xmax(a.geom) - st_xmin(a.geom)) > 1500
  OR (st_ymax(a.geom) - st_ymin(a.geom)) > 1500
  OR (st_zmax(a.geom) - st_zmin(a.geom)) > 1500;
```

## Greenhouses (Optional)
This step is performed until we have another mechanism in place which detects and rejects greenhouses before the reconstruction.
Greenhouses or other problematic cases which are characterised by glass roofs can be identified with the excessive number of building parts within a single building. 

First I identiy the buildings with > 10000 building parts (can also be 5000). Then I store them in a separate table `tiles.gpkg_files_only_greenhouses` and I  drop the same buildings from the `tiles.gpkg_files` table. 

```
select identificatie  
from  tiles.gpkg_files 
group by identificatie  
HAVING count(*) > 10000;
```

## Create the 3D tiles.

After cloning the pg2b3dm repo on godzilla, you need to activate a tunnel to gilfoyle:

```bash
ssh -f -N -M -S /tmp/gilfoyle_postgres -L 5435:localhost:5432 gilfoyle
```

Then you can build from within the root of the repo with:
```bash
  cd pg2b3dm/src/pg2b3dm
  dotnet build
```

And then run this command to create the tiles (make sure you have a .pgpass file with the credentials for the gilfoyle DB):

```bash
dotnet run -- -U <USER_NAME> -p 5435 --dbname baseregisters -t 'tiles.gpkg_files' -c 'geom' -i 'ogc_fid' --qttable tiles.quadtree --tileidcolumn tile_id --lodcolumn lod --attributescolumn attributes --skiptilesntriangles 3500000 --passfile ~/.pgpass --maxthreads 30 --compression gzip --disableprogressbar -o /data/3DBAGv3/export_v2023.10.08/3dtiles/  --skiptiles
 ```

## Command line options

All parameters are optional, except the -t --table option. 

If --username and/or --dbname are not specified the current username is used as default.

```
  -U, --username         (Default: username) Database user

  -h, --host             (Default: localhost) Database host

  -d, --dbname           (Default: username) Database name

  -c, --column           (Default: geom) Geometry column name

  -i, --idcolumn         (Default: id): Identifier column

  -t, --table            (Required) Database table name, include database schema if needed

  -o, --output           (Default: ./output/tiles) Output directory, will be created if not exists

  -p, --port             (Default: 5432) Database port

  -r, --roofcolorcolumn  (Default: '') color column name

  -a, --attributescolumn (Default: '') attributes column name 

  -e, --extenttile       (Default: 1000) Maximum extent per tile

  -g, --geometricerrors  (Default: 500, 0) Geometric errors
  
  -l, --lodcolumn        (default: '') lod column name

  --refine                  (Default: REPLACE) Refinement method (ADD/REPLACE)

  --skiptiles               (Default: false) Skip creation of existing tiles

  --maxthreads              (Default: -1) The maximum number of threads to use

  --qttable                 Required. Pre-defined quadtree full table

  --leavestable             Required. Pre-defined quadtree leaves table

  --compression             (Default: ) Tiles compression type (gzip)

  --passfile                (Default: ) Psql passfile path (.pgpass)

  --tileidcolumn            (Default: tile_id) Tile ID column

  --lod                     (Default: 22) LoD to be extracted

  --skiptilesntriangles     (Default: 0) Skip tiles with more than n triangles

  --disableprogressbar      (Default: false) Disable the progress bar
  
  --help                Display this help screen.

  --version             Display version information.  
```
