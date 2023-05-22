# Getting started

## Introduction

In this document we run pg2b3dm on a sample dataset, a shapefile from Delaware containing building footprints with a height attribute. 
The generated 3D tiles are visualized in a MapBox viewer.


## Prerequisites

Docker
GDAL (ogr2ogr)

## Download data

The dataset we will use is part of the [US Building Footprints](https://wiki.openstreetmap.org/wiki/Microsoft_Building_Footprint_Data).

Download dataset: 

[Delaware - Dover (22,532 buildings available)](https://1drv.ms/u/s!AqWv0F0N63JkgQqO6E9e2kI28R16)

Unzip the file. It contains a 'bldg_footprints.shp' shapefile with building height column.

## Setup PostGIS

1] Create Docker network

In this tutorial, we'll start 3 containers: one with PostGIS database, one with the tessellation tool and finally the tiling tool pg2b3dm. Because those containers need to communicate they must be in the same network. So we'll create a network first and add the 2 containers later.

If you have already installed a PostGIS server you can skip this step.

```bash
docker network create  mynetwork
```

2] Start PostGIS database

```bash
docker run -d --name some-postgis -e POSTGRES_PASSWORD=postgres -p 5432:5432 -it --network mynetwork mdillon/postgis
```

## Import buildings to PostGIS

Import the buildings to database using ogr2ogr.

```bash
ogr2ogr -f "PostgreSQL" PG:"host=localhost user=postgres password=postgres dbname=postgres" bldg_footprints.shp -nlt POLYGON -nln delaware_buildings
```

In PostGIS, a spatial table 'delaware_buildings' is created.

## PSQL into PostGIS

PSQL into PostGIS and do a count on the buildings:

```bash
psql -U postgres
```

```SQL
postgres=# select count(*) from delaware_buildings;
```

## Clean data

Maybe there are some invalid polygons, let's remove them first.

```SQL
postgres=# DELETE from delaware_buildings where ST_IsValid(wkb_geometry)=false;
```

### Add id field with text type

```SQL
postgres=# ALTER TABLE delaware_buildings ADD COLUMN id varchar;
postgres=# UPDATE delaware_buildings SET id = ogc_fid::text;
```

### Add column for output triangulated geometry

```SQL
postgres=# ALTER TABLE delaware_buildings ADD COLUMN  geom_triangle geometry;
```

### Colors and styling

Add two more columns to the delaware_buildings table:

```SQL
postgres=# ALTER TABLE delaware_buildings ADD COLUMN style json;
postgres=# ALTER TABLE delaware_buildings ADD COLUMN shaders json;
```

Update the style column with a JSON file containing walls, roof, floor colors:

Colors used:

#008000: green (floor)

#FF0000: rood (rood)

#EEC900: wall (geel)


```SQL
postgres=# UPDATE delaware_buildings SET style = ('{ "walls": "#EEC900", "roof":"#FF0000", "floor":"#008000"}');
```
The 'colors' column will be filled in next 'bertt/tesselate_building' step.

now exit psql:

```SQL
postgres=# exit
```

## Run bertt/tesselate_building

Run bertt/tesselate_building. It does the following:

- reads the footprint heights and geometries (from wkb_geometry);

- extrudes the buildings with height value; 

- triangulate the building and gets the colors per triangle;

- writes geometries to column geom_triangle (as polyhedralsurface geometries);

- writes colors info (color code per triangle) into colors column;

- format option -f mapbox/cesium: in the next sample the default output format is used: '-f mapbox'. 
When building for Cesium use '-f cesium'. 

```bash
docker run -it --name tessellation --network mynetwork bertt/tesselate_building -h some-postgis -U postgres -d postgres -f cesium -t delaware_buildings -i wkb_geometry -o geom_triangle --idcolumn ogc_fid --stylecolumn style --shaderscolumn shaders
```

## Run pg2b3dm

Run pg2b3dm, the program will make a connection to the database and 1 tileset.json and 927 b3dm's will be created in the output directory.

```bash
docker run -v $(pwd)/output:/app/output -it --network mynetwork geodan/pg2b3dm -h some-postgis -U postgres -c geom_triangle -t delaware_buildings -d postgres -a id,height --shaderscolumn shaders
```

## Visualize in MapBox

Required: Use -f mapbox (default option) in previous step bertt/tesselate_building.

Copy the generated tiles to sample_data\delaware\mapbox\ (overwrite the tileset.json and sample tiles in tiles directory there).

Put folder 'sample_data' on a webserver (for example https://caddyserver.com/) and navigate to /delaware/mapbox/index.html

If all goes well in Delaware - Dover you can find some 3D Tiles buildings.

![alt text](delaware_mapbox.png "Delaware MapBox")

Sample live demo in MapBox GL JS: https://geodan.github.io/pg2b3dm/sample_data/delaware/mapbox/


## Visualize in Cesium

Required: Use -f cesium in previous step bertt/tesselate_building.

Copy the generated tiles to sample_data\delaware\cesium\ (overwrite the tileset.json and sample tiles in tiles directory there).

Put folder 'sample_data' on a webserver (for example https://caddyserver.com/) and navigate to /delaware/cesium/index.html

If all goes well in Delaware - Dover you can find some 3D Tiles buildings.

![alt text](delaware_cesium.png "Delaware Cesium")

Sample live demo in Cesium: https://geodan.github.io/pg2b3dm/sample_data/delaware/cesium/
